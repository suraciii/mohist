import {
  ClientSideConnection,
  PROTOCOL_VERSION,
} from '@agentclientprotocol/sdk';
import type {
  SessionNotification,
  RequestPermissionRequest,
  RequestPermissionResponse,
} from '@agentclientprotocol/sdk';
import { AcpProcess } from './acp-process';
import type { SessionObserver, SessionContext, SessionState, ToolCallEvent, MohistPromptEvent } from './session-observer';
import { SessionStateMachine } from './session-state';
import { Log } from '../util/log';

const log = Log.create({ service: 'agent-session' });

export type PromptKind = 'initial' | 'task' | 'retry' | 'followup' | 'recovery';

export interface ExecutePromptOptions {
  kind?: PromptKind;
  title?: string;
  signal?: AbortSignal;
}

export interface AgentSessionOptions {
  cwd: string;
  task?: string;
  taskId?: string;
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  issueNumber?: number;
  onSessionUpdate?: (notification: SessionNotification) => void;
  opencodeBinPath?: string;
  signal?: AbortSignal;
  observers?: SessionObserver[];
  onProcessSpawned?: (proc: import('child_process').ChildProcess) => void;
  stage?: string;
  model?: string;
  onBeforeKill?: (cwd: string) => Promise<boolean>;
  title?: string;
  livenessQuietThresholdMs?: number;
  probeTimeoutMs?: number;
}

export interface AcpSessionResult {
  text: string;
  success: boolean;
  error?: string;
  acpSessionId?: string;
  wipCommitted?: boolean;
  failureKind?: 'session_failed' | 'timeout' | 'cancelled';
  failureReason?: string;
}

const DEFAULT_TIMEOUT = 30 * 60 * 1000;
const PER_ROUND_TIMEOUT = 15 * 60 * 1000;
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024;
const DEFAULT_LIVENESS_QUIET_THRESHOLD_MS = 5 * 60 * 1000;
const DEFAULT_PROBE_TIMEOUT_MS = 30 * 1000;

export function truncateAgentText(text: string): string {
  if (text.length <= MAX_AGENT_TEXT_LENGTH) return text;
  const keepLength = Math.floor(MAX_AGENT_TEXT_LENGTH / 2);
  const head = text.slice(0, keepLength);
  const tail = text.slice(-keepLength);
  const truncated = text.length - MAX_AGENT_TEXT_LENGTH;
  return `${head}\n\n...[truncated ${truncated} characters]...\n\n${tail}`;
}

function createTimeout(ms: number): Promise<'timeout'> {
  return new Promise<'timeout'>((resolve) => setTimeout(() => resolve('timeout'), ms));
}

class ToolCallIdGenerator {
  private counters = new Map<string, number>();
  private ids = new Map<string, string[]>();

  nextToolCallId(acpSessionId: string, toolName: string, state: 'started' | 'completed'): string {
    if (state === 'started') {
      const toolCallId = `${acpSessionId}-${toolName}-${this.counters.get(acpSessionId) ?? 0}`;
      this.counters.set(acpSessionId, (this.counters.get(acpSessionId) ?? 0) + 1);
      const key = `${acpSessionId}-${toolName}`;
      const list = this.ids.get(key) ?? [];
      list.push(toolCallId);
      this.ids.set(key, list);
      return toolCallId;
    } else {
      const key = `${acpSessionId}-${toolName}`;
      const list = this.ids.get(key) ?? [];
      const toolCallId = list.shift() ?? `${acpSessionId}-${toolName}-${this.counters.get(acpSessionId) ?? 0}`;
      if (list.length > 0) {
        this.ids.set(key, list);
      } else {
        this.ids.delete(key);
      }
      return toolCallId;
    }
  }
}

function buildRawNotificationObserver(
  onSessionUpdate: (notification: SessionNotification) => void,
): SessionObserver {
  return { onRawNotification(_ctx, n) { onSessionUpdate(n); } };
}

export class AgentSession {
  private _options: AgentSessionOptions;
  private _acpProcess: AcpProcess;
  private _connection!: ClientSideConnection;
  private _observers: SessionObserver[];
  private _wfObserver: SessionObserver | undefined;
  private _stateMachine: SessionStateMachine;
  private _sessionId = '';
  private _agentText = '';
  private _agentTextTruncated = false;
  private _closed = false;
  private _sessionStartTime: number;
  private _toolCallIdGenerator = new ToolCallIdGenerator();
  private _lastDataAt: number;
  private _probeSentAt: number | null = null;
  private _probeDeadlineAt: number | null = null;
  private _activeProbe = false;
  private _failureReason: string | null = null;
  private _livenessQuietThresholdMs: number;
  private _probeTimeoutMs: number;
  private _resetQuietTimer: (() => void) | null = null;

  get state(): SessionState { return this._stateMachine.current; }
  get acpSessionId(): string { return this._sessionId; }

  private constructor(
    options: AgentSessionOptions,
    acpProcess: AcpProcess,
    observers: SessionObserver[],
  ) {
    this._options = options;
    this._acpProcess = acpProcess;
    this._observers = observers;
    this._wfObserver = observers.find(o => typeof o.nextToolCallId === 'function');
    this._sessionStartTime = Date.now();
    this._lastDataAt = this._sessionStartTime;
    this._stateMachine = new SessionStateMachine('initializing');
    this._livenessQuietThresholdMs = options.livenessQuietThresholdMs ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS;
    this._probeTimeoutMs = options.probeTimeoutMs ?? DEFAULT_PROBE_TIMEOUT_MS;
  }

  private setupConnection(): void {
    const self = this;
    this._connection = new ClientSideConnection(
      () => ({
        sessionUpdate: async (notification: SessionNotification) => {
          try {
            self.handleSessionUpdate(notification);
          } catch (err) {
            log.error('sessionUpdate error', { error: err instanceof Error ? err.message : String(err) });
          }
        },
        requestPermission: async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
          const allow = params.options.find((o) => o.kind === 'allow_once' || o.kind === 'allow_always');
          if (allow) return { outcome: { outcome: 'selected', optionId: allow.optionId } };
          return { outcome: { outcome: 'cancelled' } };
        },
      }),
      this._acpProcess.stream,
    );
  }

  private refreshLastDataAt(): void {
    this._lastDataAt = Date.now();
    if (this._stateMachine.current === 'probing') {
      this._activeProbe = false;
      this._probeDeadlineAt = null;
      try {
        this._stateMachine.transition('running');
      } catch (err) {
        log.warn('stateMachine transition to running from probing failed', { error: err instanceof Error ? err.message : String(err) });
      }
      const ctx = this.makeCtx();
      for (const obs of this._observers) {
        try { obs.onStateChange?.(ctx, 'probing', 'running'); } catch (err) {
          log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
        }
      }
      this.notifyLivenessUpdate(ctx, 'running');
    }
    if (this._resetQuietTimer && this._stateMachine.current === 'running') {
      this._resetQuietTimer();
    }
  }

  private notifyLivenessUpdate(ctx: SessionContext, status: SessionState, extra?: Partial<{ lastDataAt: string | null; probeSentAt: string | null; probeDeadlineAt: string | null; failureReason: string | null }>): void {
    for (const obs of this._observers) {
      try {
        obs.onLivenessUpdate?.(ctx, {
          status,
          lastDataAt: extra?.lastDataAt ?? new Date(this._lastDataAt).toISOString(),
          probeSentAt: extra?.probeSentAt ?? (this._probeSentAt ? new Date(this._probeSentAt).toISOString() : null),
          probeDeadlineAt: extra?.probeDeadlineAt ?? (this._probeDeadlineAt ? new Date(this._probeDeadlineAt).toISOString() : null),
          failureReason: extra?.failureReason ?? this._failureReason,
        });
      } catch (err) {
        log.error('onLivenessUpdate observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
  }

  private transitionToFailed(reason: string): void {
    this._failureReason = reason ?? 'unknown';
    try {
      this._stateMachine.transition('failed');
    } catch (err) {
      log.warn('stateMachine transition to failed failed', { error: err instanceof Error ? err.message : String(err) });
    }
    const ctx = this.makeCtx();
    this.notifyLivenessUpdate(ctx, 'failed');
  }

  private async startProbe(): Promise<void> {
    this._probeSentAt = Date.now();
    this._probeDeadlineAt = this._probeSentAt + this._probeTimeoutMs;
    this._activeProbe = true;

    const fromState = this._stateMachine.current;
    try {
      this._stateMachine.transition('probing');
    } catch (err) {
      log.warn('stateMachine transition to probing failed', { error: err instanceof Error ? err.message : String(err) });
    }

    const ctx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(ctx, fromState, 'probing'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    this.notifyLivenessUpdate(ctx, 'probing');

    const probeText = 'If this session is still alive, briefly report the current step and continue from existing context. Do not restart completed work.';

    this._connection.prompt({
      sessionId: this._sessionId,
      prompt: [{ type: 'text', text: probeText }],
    }).catch((err) => {
      log.warn('probe prompt failed', { sessionId: this._sessionId, error: err instanceof Error ? err.message : String(err) });
    });
  }

  private handleSessionUpdate(notification: SessionNotification): void {
    this.refreshLastDataAt();
    const update = notification.update;
    const eventType = update.sessionUpdate;

    if (eventType === 'agent_thought_chunk') {
      // no special handling
    } else if (
      eventType === 'agent_message_chunk' &&
      update.content &&
      'text' in update.content
    ) {
      const textChunk = (update.content as { text: string }).text;
      if (!this._agentTextTruncated) {
        this._agentText += textChunk;
        if (this._agentText.length > MAX_AGENT_TEXT_LENGTH) {
          this._agentText = truncateAgentText(this._agentText);
          this._agentTextTruncated = true;
        }
      }
      const ctx = this.makeCtx();
      for (const obs of this._observers) {
        try { obs.onTextChunk?.(ctx, textChunk); } catch (err) {
          log.error('onTextChunk observer failed', { error: err instanceof Error ? err.message : String(err) });
        }
      }
    }

    if (eventType === 'tool_call') {
      const toolData = update as Record<string, unknown>;
      const toolCallData = toolData.toolCall as Record<string, unknown> | undefined;
      const toolStatus = (toolCallData?.status as string) ?? '';
      const state = toolStatus === 'completed' ? 'completed' as const : 'started' as const;
      const toolName = (toolCallData?.toolName as string) ?? '';
      const existingToolCallId = (toolCallData?.toolCallId as string | undefined)
        ?? (toolCallData?.id as string | undefined)
        ?? (toolCallData?.callId as string | undefined);
      const wfObs = this._wfObserver;
      let toolCallId: string;
      if (existingToolCallId) {
        toolCallId = existingToolCallId;
      } else if (wfObs) {
        toolCallId = wfObs.nextToolCallId!(this._sessionId, toolName, state);
      } else {
        toolCallId = `${this._sessionId}-${toolName}-0`;
      }
      if (toolCallData && !existingToolCallId) {
        toolCallData.toolCallId = toolCallId;
      } else if (!toolCallData && !existingToolCallId) {
        toolData.toolCall = { toolCallId };
      }
    }

    {
      const ctx = this.makeCtx();
      for (const obs of this._observers) {
        try { obs.onSessionEvent?.(ctx, eventType, update); } catch (err) {
          log.error('onSessionEvent observer failed', { eventType, error: err instanceof Error ? err.message : String(err) });
        }
      }
      for (const obs of this._observers) {
        try { obs.onRawNotification?.(ctx, notification); } catch (err) {
          log.error('onRawNotification observer failed', { error: err instanceof Error ? err.message : String(err) });
        }
      }
    }

    if (eventType === 'tool_call') {
      const toolData = update as Record<string, unknown>;
      const toolCallData = toolData.toolCall as Record<string, unknown> | undefined;
      const toolStatus = (toolCallData?.status as string) ?? '';
      const state = toolStatus === 'completed' ? 'completed' as const : 'started' as const;
      const toolName = (toolCallData?.toolName as string) ?? '';
      const toolCallId = this._toolCallIdGenerator.nextToolCallId(this._sessionId, toolName, state);
      const ctx = this.makeCtx();
      const event: ToolCallEvent = {
        toolName,
        state,
        toolCallId,
        title: (toolCallData?.title as string) ?? undefined,
        rawInput: state === 'started' ? toolCallData?.input : undefined,
        rawOutput: state === 'completed' ? toolCallData?.output : undefined,
        rawOutputMetadata: state === 'completed' ? ((toolCallData?.output as Record<string, unknown>)?.metadata as Record<string, unknown> | undefined) : undefined,
        status: toolStatus || undefined,
      };
      for (const obs of this._observers) {
        try { obs.onToolCall?.(ctx, event); } catch (err) {
          log.error('onToolCall observer failed', { toolName: event.toolName, error: err instanceof Error ? err.message : String(err) });
        }
      }
    }
  }

  private makeCtx(): SessionContext {
    return {
      issueId: this._options.issueId ?? '',
      issueNumber: this._options.issueNumber,
      projectId: this._options.projectId ?? '',
      executionId: this._options.executionId,
      acpSessionId: this._sessionId,
      stage: this._options.stage,
      model: this._options.model,
      processPid: this._acpProcess.process.pid ?? undefined,
    };
  }

  private buildPromptData(prompt: string, kind: string, sentAt: string, title?: string): MohistPromptEvent {
    const outputPath = this.extractOutputPath(prompt);
    const contextFiles = this.extractContextFiles(prompt);
    return {
      role: 'mohist',
      text: prompt,
      kind,
      sentAt,
      executionId: this._options.executionId,
      stage: this._options.stage,
      title: title ?? this._options.title,
      issueId: this._options.issueId,
      acpSessionId: this._sessionId,
      outputPath,
      contextFiles,
    };
  }

  private extractOutputPath(prompt: string): string | undefined {
    const match = prompt.match(/<contract>([\s\S]*?)<\/contract>/i);
    if (match) {
      return match[1].trim().split('\n')[0].trim();
    }
    return undefined;
  }

  private extractContextFiles(prompt: string): string[] | undefined {
    const match = prompt.match(/<context_files>([\s\S]*?)<\/context_files>/i);
    if (match) {
      const files = match[1].trim().split('\n').map(f => f.trim()).filter(f => f);
      return files.length > 0 ? files.slice(0, 5) : undefined;
    }
    return undefined;
  }

  static async create(options: AgentSessionOptions): Promise<AgentSession> {
    const {
      cwd,
      timeout = PER_ROUND_TIMEOUT,
      issueId,
      model,
    } = options;

    const observers: SessionObserver[] = [];
    if (options.onSessionUpdate) {
      observers.push(buildRawNotificationObserver(options.onSessionUpdate));
    }
    observers.push(...(options.observers ?? []));

    log.info('Spawning opencode acp subprocess for agent session', {
      cwd, timeout, issueId: issueId?.slice(0, 8), taskId: options.taskId, promptPreview: (options.task ?? '').slice(0, 100),
    });

    const acpProcess = new AcpProcess({
      cwd,
      opencodeBinPath: options.opencodeBinPath,
      onError: (err) => {
        const ctx = { issueId: issueId ?? '', acpSessionId: '', stage: options.stage, model: options.model, processPid: acpProcess.process.pid ?? undefined, executionId: options.executionId, projectId: options.projectId ?? '', issueNumber: options.issueNumber };
        for (const obs of observers) {
          try { obs.onSessionEvent?.(ctx, 'acp_session_process_error', { error: err.message, mode: 'agent-session', timestamp: new Date().toISOString() }); } catch (e) { /* ignore */ }
        }
      },
      onExit: ({ exitCode, phase }) => {
        const ctx = { issueId: issueId ?? '', acpSessionId: '', stage: options.stage, model: options.model, processPid: acpProcess.process.pid ?? undefined, executionId: options.executionId, projectId: options.projectId ?? '', issueNumber: options.issueNumber };
        for (const obs of observers) {
          try { obs.onSessionEvent?.(ctx, 'acp_session_process_exit', { exitCode, phase, mode: 'agent-session', timestamp: new Date().toISOString() }); } catch (e) { /* ignore */ }
        }
      },
    });

    if (options.onProcessSpawned) {
      options.onProcessSpawned(acpProcess.process);
    }

    const session = new AgentSession(options, acpProcess, observers);
    session.setupConnection();

    try {
      const initResult = await Promise.race([
        session._connection.initialize({
          protocolVersion: PROTOCOL_VERSION,
          clientInfo: { name: 'mohist', version: '0.1.0' },
        }),
        createTimeout(timeout),
        acpProcess.spawnFailure,
      ]).catch(async (err: unknown) => {
        await acpProcess.cleanup();
        throw err;
      });

      acpProcess.markInitialized();

      if (initResult === 'timeout') {
        const duration = Date.now() - session._sessionStartTime;
        log.error('ACP initialize timed out', { timeout, elapsedMs: duration });
        if (options.onBeforeKill) {
          try { await options.onBeforeKill(options.cwd); } catch (err) {
            log.warn('onBeforeKill failed on init timeout', { error: err instanceof Error ? err.message : String(err) });
          }
        }
        await acpProcess.cleanup();
        throw new Error('[INIT_TIMEOUT] ACP initialize timed out');
      }

      log.info('ACP initialized, creating session');

      const sessionResult = await Promise.race([
        session._connection.newSession({ cwd, mcpServers: [] }),
        createTimeout(timeout),
      ]);

      if (sessionResult === 'timeout') {
        const duration = Date.now() - session._sessionStartTime;
        log.error('ACP newSession timed out', { timeout, elapsedMs: duration });
        if (options.onBeforeKill) {
          try { await options.onBeforeKill(options.cwd); } catch (err) {
            log.warn('onBeforeKill failed on newSession timeout', { error: err instanceof Error ? err.message : String(err) });
          }
        }
        await acpProcess.cleanup();
        throw new Error('[NEWSESSION_TIMEOUT] ACP newSession timed out');
      }

      session._sessionId = sessionResult.sessionId;
      log.info('ACP session created', { sessionId: session._sessionId });

      if (model) {
        try {
          await session._connection.setSessionConfigOption({
            sessionId: session._sessionId, configId: 'model', value: model,
          });
          log.info('ACP session model set', { sessionId: session._sessionId, model });
        } catch (err) {
          session._options = { ...session._options, model: undefined };
          log.warn('setSessionConfigOption for model failed', {
            sessionId: session._sessionId, model, error: err instanceof Error ? err.message : String(err),
          });
        }
      }
    } catch (err) {
      throw err;
    }

    session._stateMachine.transition('running');
    const startCtx = session.makeCtx();
    for (const obs of session._observers) {
      try { obs.onSessionStart?.(startCtx); } catch (err) {
        log.error('onSessionStart observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }

    return session;
  }

  async execute(prompt: string, meta?: ExecutePromptOptions): Promise<AcpSessionResult> {
    if (this._closed) {
      return { text: '', success: false, error: 'Session is closed' };
    }

    const roundStartIndex = this._agentText.length;
    const timeout = this._options.timeout ?? PER_ROUND_TIMEOUT;

    const kind = meta?.kind ?? 'task';
    const sentAt = new Date().toISOString();

    const wfObs2 = this._wfObserver;
    if (wfObs2) {
      const promptData = this.buildPromptData(prompt, kind, sentAt, meta?.title);
      wfObs2.writeMohistPrompt!(this.makeCtx(), promptData);
    }

    const execSignal = meta?.signal ?? this._options.signal;
    const abortPromise = execSignal
      ? new Promise<'aborted'>((resolve) => {
          if (execSignal.aborted) { resolve('aborted'); return; }
          const onAbort = () => resolve('aborted');
          execSignal.addEventListener('abort', onAbort, { once: true });
        })
      : new Promise<'aborted'>(() => {});

    const quietThresholdMonitor = (() => {
      let timer: ReturnType<typeof setTimeout> | null = null;
      let currentPromise: Promise<'quiet_threshold'> | null = null;

      const start = () => {
        if (timer) return;
        currentPromise = new Promise<'quiet_threshold'>((resolve) => {
          timer = setTimeout(() => {
            timer = null;
            resolve('quiet_threshold');
          }, this._livenessQuietThresholdMs);
        });
      };

      const restart = () => {
        if (timer !== null) { clearTimeout(timer); timer = null; }
        currentPromise = null;
        start();
      };

      const clear = () => {
        if (timer !== null) { clearTimeout(timer); timer = null; }
        currentPromise = null;
      };

      const promise = () => currentPromise ?? new Promise<'quiet_threshold'>(() => {});

      return { start, restart, clear, promise };
    })();

    this._resetQuietTimer = () => quietThresholdMonitor.restart();
    quietThresholdMonitor.start();

    try {
      const promptPromise = this._connection.prompt({
        sessionId: this._sessionId,
        prompt: [{ type: 'text', text: prompt }],
      });

      while (true) {
        const checkQuiet = () => {
          if (Date.now() - this._lastDataAt >= this._livenessQuietThresholdMs && this._stateMachine.current === 'running') {
            return true;
          }
          return false;
        };

        const probeDeadlinePromise = this._probeDeadlineAt
          ? new Promise<'probe_deadline'>((resolve) => {
              const ms = this._probeDeadlineAt! - Date.now();
              if (ms <= 0) { resolve('probe_deadline'); return; }
              setTimeout(() => resolve('probe_deadline'), ms);
            })
          : new Promise<'probe_deadline'>(() => {});

        const result = await Promise.race([
          promptPromise,
          createTimeout(timeout),
          abortPromise,
          this._acpProcess.exitFailure,
          probeDeadlinePromise,
          quietThresholdMonitor.promise(),
        ]);

        if (result === 'quiet_threshold') {
          if (checkQuiet()) {
            quietThresholdMonitor.clear();
            await this.startProbe();
            quietThresholdMonitor.start();
          } else {
            quietThresholdMonitor.restart();
          }
          continue;
        }

        if (result === 'probe_deadline') {
          if (this._activeProbe && !this._closed) {
            quietThresholdMonitor.clear();
            this._failureReason = 'probe_timeout';
            this.transitionToFailed('Probe deadline expired');
            const failCtx = this.makeCtx();
            for (const obs of this._observers) {
              try { obs.onStateChange?.(failCtx, 'probing', 'failed'); } catch (err) {
                log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
              }
            }
            await this._acpProcess.cleanup();
            this._closed = true;
            return {
              text: this._agentText.slice(roundStartIndex),
              success: false,
              error: 'Session liveness probe timed out',
              acpSessionId: this._sessionId,
              failureKind: 'session_failed',
              failureReason: this._failureReason,
            };
          }
          this._probeDeadlineAt = null;
          continue;
        }

        if (result === 'aborted') {
          quietThresholdMonitor.clear();
          log.info('ACP prompt aborted by signal', { sessionId: this._sessionId });
          try { this._stateMachine.transition('cancelled'); } catch (err) {
            log.warn('stateMachine transition to cancelled failed', { error: err instanceof Error ? err.message : String(err) });
          }
          const cancelCtx = this.makeCtx();
          for (const obs of this._observers) {
            try { obs.onStateChange?.(cancelCtx, 'running', 'cancelled'); } catch (err) {
              log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
            }
          }
          try { await this._connection.cancel({ sessionId: this._sessionId }); } catch (err) {
            log.warn('cancel failed on abort', { sessionId: this._sessionId, error: err instanceof Error ? err.message : String(err) });
          }
          let wipCommitted = false;
          if (this._options.onBeforeKill) {
            try { wipCommitted = await this._options.onBeforeKill(this._options.cwd); } catch (err) {
              log.warn('onBeforeKill failed on abort', { error: err instanceof Error ? err.message : String(err) });
            }
          }
          await this._acpProcess.cleanup();
          this._closed = true;
          return {
            text: this._agentText.slice(roundStartIndex),
            success: false,
            error: 'Agent stopped by user',
            acpSessionId: this._sessionId,
            wipCommitted,
            failureKind: 'cancelled',
          };
        }

        if (result === 'timeout') {
          quietThresholdMonitor.clear();
          const duration = Date.now() - this._sessionStartTime;
          log.error('ACP prompt timed out', { sessionId: this._sessionId, timeout, elapsedMs: duration });
          try { this._stateMachine.transition('timeout'); } catch (err) {
            log.warn('stateMachine transition to timeout failed', { error: err instanceof Error ? err.message : String(err) });
          }
          const failCtx = this.makeCtx();
          for (const obs of this._observers) {
            try { obs.onStateChange?.(failCtx, 'running', 'timeout'); } catch (err) {
              log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
            }
          }
          try { await this._connection.cancel({ sessionId: this._sessionId }); } catch (err) {
            log.warn('cancel failed on timeout', { sessionId: this._sessionId, error: err instanceof Error ? err.message : String(err) });
          }
          let wipCommitted = false;
          if (this._options.onBeforeKill) {
            try { wipCommitted = await this._options.onBeforeKill(this._options.cwd); } catch (err) {
              log.warn('onBeforeKill failed on timeout', { error: err instanceof Error ? err.message : String(err) });
            }
          }
          this._closed = true;
          await this._acpProcess.cleanup();
          return {
            text: this._agentText.slice(roundStartIndex),
            success: false,
            error: `Timed out after ${timeout / 1000}s`,
            acpSessionId: this._sessionId,
            wipCommitted,
            failureKind: 'timeout',
          };
        }

        quietThresholdMonitor.clear();
        return {
          text: this._agentText.slice(roundStartIndex),
          success: true,
          acpSessionId: this._sessionId,
        };
      }
    } catch (err) {
      quietThresholdMonitor.clear();
      const duration = Date.now() - this._sessionStartTime;
      log.error('ACP execute failed', {
        sessionId: this._sessionId, elapsedMs: duration, error: err instanceof Error ? err.message : String(err),
      });
      this._failureReason = err instanceof Error ? err.message : String(err);
      this.transitionToFailed('Execute error');
      const failCtx = this.makeCtx();
      for (const obs of this._observers) {
        try { obs.onStateChange?.(failCtx, 'running', 'failed'); } catch (obsErr) {
          log.error('onStateChange observer failed', { error: obsErr instanceof Error ? obsErr.message : String(obsErr) });
        }
      }
      let wipCommitted = false;
      if (this._options.onBeforeKill) {
        try { wipCommitted = await this._options.onBeforeKill(this._options.cwd); } catch (killErr) {
          log.warn('onBeforeKill failed on error', { error: killErr instanceof Error ? killErr.message : String(killErr) });
        }
      }
      await this._acpProcess.cleanup();
      const message = err instanceof Error ? err.message : String(err);
      return { text: this._agentText.slice(roundStartIndex), success: false, error: message, wipCommitted, failureKind: 'session_failed', failureReason: this._failureReason };
    } finally {
      this._resetQuietTimer = null;
    }
  }

  async cancel(): Promise<void> {
    if (this._closed) return;
    try { await this._connection.cancel({ sessionId: this._sessionId }); } catch (err) {
      log.warn('cancel failed', { sessionId: this._sessionId, error: err instanceof Error ? err.message : String(err) });
    }
    try { this._stateMachine.transition('cancelled'); } catch (err) {
      log.warn('stateMachine transition to cancelled failed', { error: err instanceof Error ? err.message : String(err) });
    }
    const ctx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(ctx, 'running', 'cancelled'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
  }

  async close(): Promise<void> {
    if (this._closed) return;
    this._closed = true;
    const duration = Date.now() - this._sessionStartTime;
    log.info('Agent session closed', { sessionId: this._sessionId, elapsedMs: duration });
    try { this._stateMachine.transition('completed'); } catch (err) {
      log.warn('stateMachine transition to completed failed', { error: err instanceof Error ? err.message : String(err) });
    }
    const ctx = this.makeCtx();
    for (const obs of this._observers) {
      try { obs.onStateChange?.(ctx, 'running', 'completed'); } catch (err) {
        log.error('onStateChange observer failed', { error: err instanceof Error ? err.message : String(err) });
      }
    }
    await this._acpProcess.cleanup();
  }
}

export function withSession(
  options: AgentSessionOptions,
): Promise<AcpSessionResult>;
export function withSession<T>(
  options: AgentSessionOptions,
  fn: (session: AgentSession) => Promise<T>,
): Promise<T>;
export async function withSession<T = AcpSessionResult>(
  options: AgentSessionOptions,
  fn?: (session: AgentSession) => Promise<T>,
): Promise<AcpSessionResult | T> {
  let session: AgentSession;
  try {
    session = await AgentSession.create({
      ...options,
      timeout: options.timeout ?? DEFAULT_TIMEOUT,
    });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    return { text: '', success: false, error: message };
  }
  try {
    if (fn) return await fn(session);
    return await session.execute(options.task ?? '', { kind: 'initial' });
  } finally {
    await session.close();
  }
}
