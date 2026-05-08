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
import type { SessionObserver, SessionContext, SessionState, ToolCallEvent } from './session-observer';
import { SessionStateMachine } from './session-state';
import { Log } from '../util/log';

const log = Log.create({ service: 'agent-session' });

export type PromptKind = 'initial' | 'task' | 'retry' | 'followup' | 'recovery';

export interface ExecutePromptOptions {
  kind?: PromptKind;
  title?: string;
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
}

export interface AcpSessionResult {
  text: string;
  success: boolean;
  error?: string;
  acpSessionId?: string;
  wipCommitted?: boolean;
}

const DEFAULT_TIMEOUT = 30 * 60 * 1000;
const PER_ROUND_TIMEOUT = 15 * 60 * 1000;
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024;

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
    this._stateMachine = new SessionStateMachine('initializing');
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

  private handleSessionUpdate(notification: SessionNotification): void {
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
      wfObs2.writeMohistPrompt!(this.makeCtx(), {
        role: 'mohist',
        text: prompt,
        kind,
        sentAt,
        executionId: this._options.executionId,
        stage: this._options.stage,
        title: meta?.title ?? this._options.title,
        issueId: this._options.issueId,
        acpSessionId: this._sessionId,
      });
    }

    const abortPromise = this._options.signal
      ? new Promise<'aborted'>((resolve) => {
          if (this._options.signal!.aborted) { resolve('aborted'); return; }
          const onAbort = () => resolve('aborted');
          this._options.signal!.addEventListener('abort', onAbort, { once: true });
        })
      : new Promise<'aborted'>(() => {});

    try {
      const promptResult = await Promise.race([
        this._connection.prompt({
          sessionId: this._sessionId,
          prompt: [{ type: 'text', text: prompt }],
        }),
        createTimeout(timeout),
        abortPromise,
        this._acpProcess.exitFailure,
      ]);

      if (promptResult === 'aborted') {
        log.info('ACP prompt aborted by signal', { sessionId: this._sessionId });
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
        };
      }

      if (promptResult === 'timeout') {
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
        };
      }

      return {
        text: this._agentText.slice(roundStartIndex),
        success: true,
        acpSessionId: this._sessionId,
      };
    } catch (err) {
      const duration = Date.now() - this._sessionStartTime;
      log.error('ACP execute failed', {
        sessionId: this._sessionId, elapsedMs: duration, error: err instanceof Error ? err.message : String(err),
      });
      try { this._stateMachine.transition('failed'); } catch (stateErr) {
        log.warn('stateMachine transition to failed failed', { error: stateErr instanceof Error ? stateErr.message : String(stateErr) });
      }
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
      return { text: this._agentText.slice(roundStartIndex), success: false, error: message, wipCommitted };
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
