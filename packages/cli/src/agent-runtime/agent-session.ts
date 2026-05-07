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
import { WorkflowSessionObserver } from './session-observer';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { EventBus } from '../services/event-bus';
import { Log } from '../util/log';

const log = Log.create({ service: 'agent-session' });

export interface AgentSessionOptions {
  cwd: string;
  task?: string;
  taskId?: string;
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  workflowLogRepo?: WorkflowLogRepo;
  sessionStreamLogRepo?: SessionStreamLogRepo;
  eventBus?: EventBus;
  throttleMs?: number;
  coderSessionRepo?: CoderSessionRepo;
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

function buildWorkflowObserver(
  options: AgentSessionOptions,
  meta?: { taskDescription?: string; title?: string; stage?: string },
): WorkflowSessionObserver | undefined {
  if (!options.eventBus && !options.workflowLogRepo && !options.sessionStreamLogRepo && !options.coderSessionRepo) {
    return undefined;
  }
  return new WorkflowSessionObserver({
    eventBus: options.eventBus,
    workflowLogRepo: options.workflowLogRepo,
    sessionStreamLogRepo: options.sessionStreamLogRepo,
    coderSessionRepo: options.coderSessionRepo,
    throttleMs: options.throttleMs,
    taskDescription: meta?.taskDescription,
    title: meta?.title,
    stage: meta?.stage,
  });
}

function createTimeout(ms: number): Promise<'timeout'> {
  return new Promise<'timeout'>((resolve) => setTimeout(() => resolve('timeout'), ms));
}

export class AgentSession {
  private _options: AgentSessionOptions;
  private _acpProcess: AcpProcess;
  private _connection!: ClientSideConnection;
  private _observers: SessionObserver[];
  private _wfObserver: WorkflowSessionObserver | undefined;
  private _sessionId = '';
  private _agentText = '';
  private _agentTextTruncated = false;
  private _state: SessionState = 'initializing';
  private _closed = false;
  private _sessionStartTime: number;

  get state(): SessionState { return this._state; }
  get acpSessionId(): string { return this._sessionId; }

  private constructor(
    options: AgentSessionOptions,
    acpProcess: AcpProcess,
    observers: SessionObserver[],
    wfObserver: WorkflowSessionObserver | undefined,
  ) {
    this._options = options;
    this._acpProcess = acpProcess;
    this._observers = observers;
    this._wfObserver = wfObserver;
    this._sessionStartTime = Date.now();
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
      for (const obs of this._observers) { try { obs.onTextChunk?.(ctx, textChunk); } catch {} }
    }

    {
      const ctx = this.makeCtx();
      for (const obs of this._observers) { try { obs.onSessionEvent?.(ctx, eventType, update); } catch {} }
      for (const obs of this._observers) { try { obs.onRawNotification?.(ctx, notification); } catch {} }
    }

    if (eventType === 'tool_call') {
      const toolData = update as Record<string, unknown>;
      const toolCallData = toolData.toolCall as Record<string, unknown> | undefined;
      const toolStatus = (toolCallData?.status as string) ?? '';
      const state = toolStatus === 'completed' ? 'completed' as const : 'started' as const;
      const toolName = (toolCallData?.toolName as string) ?? '';
      const toolCallId = this._wfObserver
        ? this._wfObserver.nextToolCallId(this._sessionId, toolName, state)
        : `${this._sessionId}-${toolName}-0`;
      const ctx = this.makeCtx();
      const event: ToolCallEvent = {
        toolName,
        state,
        toolCallId,
        title: (toolCallData?.title as string) ?? undefined,
        rawInput: state === 'started' ? toolCallData?.input : undefined,
        rawOutput: state === 'completed' ? toolCallData?.output : undefined,
      };
      for (const obs of this._observers) { try { obs.onToolCall?.(ctx, event); } catch {} }
    }
  }

  private makeCtx(): SessionContext {
    return {
      issueId: this._options.issueId ?? '',
      issueNumber: this._options.issueNumber,
      projectId: this._options.projectId ?? '',
      executionId: this._options.executionId,
      acpSessionId: this._sessionId,
      coderSessionId: this._wfObserver?.coderSessionId,
      stage: this._options.stage,
      model: this._options.model,
    };
  }

  static async create(options: AgentSessionOptions): Promise<AgentSession> {
    const {
      cwd,
      timeout = PER_ROUND_TIMEOUT,
      issueId,
      model,
      stage,
      title,
    } = options;

    const wfObserver = buildWorkflowObserver(options, {
      taskDescription: 'multi-round agent session',
      title,
      stage,
    });
    const extraObservers: SessionObserver[] = [];
    if (options.onSessionUpdate) {
      const cb = options.onSessionUpdate;
      extraObservers.push({ onRawNotification(_ctx, n) { cb(n); } });
    }
    extraObservers.push(...(options.observers ?? []));
    const observers: SessionObserver[] = wfObserver ? [wfObserver, ...extraObservers] : [...extraObservers];

    log.info('Spawning opencode acp subprocess for agent session', {
      cwd, timeout, issueId: issueId?.slice(0, 8), taskId: options.taskId, promptPreview: (options.task ?? '').slice(0, 100),
    });
    wfObserver?.writeSessionLog(issueId, 'acp_session_start', {
      cwd, timeout, issueId: issueId?.slice(0, 8), taskId: options.taskId,
      promptPreview: (options.task ?? '').slice(0, 100),
      mode: 'agent-session', timestamp: new Date().toISOString(),
    });

    const acpProcess = new AcpProcess({
      cwd,
      opencodeBinPath: options.opencodeBinPath,
      onError: (err) => {
        wfObserver?.writeSessionLog(issueId, 'acp_session_process_error', {
          error: err.message, mode: 'agent-session', timestamp: new Date().toISOString(),
        });
      },
      onExit: ({ exitCode, phase }) => {
        wfObserver?.writeSessionLog(issueId, 'acp_session_process_exit', {
          exitCode, phase, mode: 'agent-session', timestamp: new Date().toISOString(),
        });
      },
    });

    if (options.onProcessSpawned) {
      options.onProcessSpawned(acpProcess.process);
    }

    const session = new AgentSession(options, acpProcess, observers, wfObserver);
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
        wfObserver?.writeSessionLog(issueId, 'acp_session_timeout', {
          phase: 'initialize', timeout, duration, mode: 'agent-session', timestamp: new Date().toISOString(),
        });
        await acpProcess.cleanup();
        throw new Error('Timed out during initialize');
      }

      log.info('ACP initialized, creating session');

      const sessionResult = await Promise.race([
        session._connection.newSession({ cwd, mcpServers: [] }),
        createTimeout(timeout),
      ]);

      if (sessionResult === 'timeout') {
        const duration = Date.now() - session._sessionStartTime;
        log.error('ACP newSession timed out', { timeout, elapsedMs: duration });
        wfObserver?.writeSessionLog(issueId, 'acp_session_timeout', {
          phase: 'newSession', timeout, duration, mode: 'agent-session', timestamp: new Date().toISOString(),
        });
        await acpProcess.cleanup();
        throw new Error('Timed out during newSession');
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
          log.warn('setSessionConfigOption for model failed', {
            sessionId: session._sessionId, model, error: err instanceof Error ? err.message : String(err),
          });
        }
      }
    } catch (err) {
      throw err;
    }

    session._state = 'running';
    const startCtx = session.makeCtx();
    for (const obs of observers) { try { obs.onSessionStart?.(startCtx); } catch {} }

    return session;
  }

  async execute(prompt: string): Promise<AcpSessionResult> {
    if (this._closed) {
      return { text: '', success: false, error: 'Session is closed' };
    }

    const roundStartIndex = this._agentText.length;
    const timeout = this._options.timeout ?? PER_ROUND_TIMEOUT;

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
        this._wfObserver?.writeSessionLog(this._options.issueId, 'acp_session_aborted', {
          sessionId: this._sessionId, mode: 'agent-session', timestamp: new Date().toISOString(),
        });
        try { await this._connection.cancel({ sessionId: this._sessionId }); } catch {}
        await this._acpProcess.cleanup();
        this._closed = true;
        return {
          text: this._agentText.slice(roundStartIndex),
          success: false,
          error: 'Agent stopped by user',
          acpSessionId: this._sessionId,
        };
      }

      if (promptResult === 'timeout') {
        const duration = Date.now() - this._sessionStartTime;
        log.error('ACP prompt timed out', { sessionId: this._sessionId, timeout, elapsedMs: duration });
        this._wfObserver?.writeSessionLog(this._options.issueId, 'acp_session_timeout', {
          phase: 'prompt', sessionId: this._sessionId, timeout, duration, mode: 'agent-session', timestamp: new Date().toISOString(),
        });
        const failCtx = this.makeCtx();
        for (const obs of this._observers) { try { obs.onStateChange?.(failCtx, 'running', 'failed'); } catch {} }
        try { await this._connection.cancel({ sessionId: this._sessionId }); } catch {}
        return {
          text: this._agentText.slice(roundStartIndex),
          success: false,
          error: `Timed out after ${timeout / 1000}s`,
          acpSessionId: this._sessionId,
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
      this._wfObserver?.writeSessionLog(this._options.issueId, 'acp_session_completed', {
        sessionId: this._sessionId, success: false, duration,
        error: err instanceof Error ? err.message : String(err), mode: 'agent-session', timestamp: new Date().toISOString(),
      });
      const failCtx = this.makeCtx();
      for (const obs of this._observers) { try { obs.onStateChange?.(failCtx, 'running', 'failed'); } catch {} }
      await this._acpProcess.cleanup();
      const message = err instanceof Error ? err.message : String(err);
      return { text: this._agentText.slice(roundStartIndex), success: false, error: message };
    }
  }

  async cancel(): Promise<void> {
    if (this._closed) return;
    try { await this._connection.cancel({ sessionId: this._sessionId }); } catch {}
    const ctx = this.makeCtx();
    for (const obs of this._observers) { try { obs.onStateChange?.(ctx, 'running', 'cancelled'); } catch {} }
  }

  async close(): Promise<void> {
    if (this._closed) return;
    this._closed = true;
    const duration = Date.now() - this._sessionStartTime;
    log.info('Agent session closed', { sessionId: this._sessionId, elapsedMs: duration });
    this._wfObserver?.writeSessionLog(this._options.issueId, 'acp_session_completed', {
      sessionId: this._sessionId, success: true, duration, mode: 'agent-session', timestamp: new Date().toISOString(),
    });
    const ctx = this.makeCtx();
    for (const obs of this._observers) { try { obs.onStateChange?.(ctx, 'running', 'completed'); } catch {} }
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
    return await session.execute(options.task ?? '');
  } finally {
    await session.close();
  }
}
