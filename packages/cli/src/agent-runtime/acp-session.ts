import {
  ClientSideConnection,
  PROTOCOL_VERSION,
} from '@agentclientprotocol/sdk';
import { AcpProcess } from './acp-process';
import type {
  SessionNotification,
  RequestPermissionRequest,
  RequestPermissionResponse,
} from '@agentclientprotocol/sdk';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { EventBus } from '../services/event-bus';
import { Log } from '../util/log';
import type { SessionObserver, SessionContext, SessionState, ToolCallEvent } from './session-observer';
import { WorkflowSessionObserver } from './session-observer';

export type { SessionObserver, SessionContext, SessionState, ToolCallEvent };

const log = Log.create({ service: 'acp-session' });

class RawNotificationBridge implements SessionObserver {
  constructor(private callback: (notification: SessionNotification) => void) {}
  onRawNotification(_ctx: SessionContext, notification: SessionNotification): void {
    this.callback(notification);
  }
}

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

export const SESSION_STREAM_EVENT_TYPES = new Set([
  'agent_thought_chunk',
  'agent_message_chunk',
  'tool_call',
  'tool_call_update',
  'user_message_chunk',
]);

const DEFAULT_TIMEOUT = 30 * 60 * 1000;
const PER_ROUND_TIMEOUT = 15 * 60 * 1000;
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024;

export function truncateAgentText(text: string): string {
  if (text.length <= MAX_AGENT_TEXT_LENGTH) {
    return text;
  }
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

function makeCtx(options: AgentSessionOptions, acpSessionId: string, coderSessionId?: string): SessionContext {
  return {
    issueId: options.issueId ?? '',
    issueNumber: options.issueNumber,
    projectId: options.projectId ?? '',
    executionId: options.executionId,
    acpSessionId,
    coderSessionId,
    stage: options.stage,
    model: options.model,
  };
}

function dispatchSessionStart(observers: SessionObserver[], ctx: SessionContext): void {
  for (const obs of observers) {
    try { obs.onSessionStart?.(ctx); } catch {}
  }
}

function dispatchTextChunk(observers: SessionObserver[], ctx: SessionContext, text: string): void {
  for (const obs of observers) {
    try { obs.onTextChunk?.(ctx, text); } catch {}
  }
}

function dispatchSessionEvent(observers: SessionObserver[], ctx: SessionContext, eventType: string, data: unknown): void {
  for (const obs of observers) {
    try { obs.onSessionEvent?.(ctx, eventType, data); } catch {}
  }
}

function dispatchStateChange(observers: SessionObserver[], ctx: SessionContext, from: SessionState, to: SessionState): void {
  for (const obs of observers) {
    try { obs.onStateChange?.(ctx, from, to); } catch {}
  }
}

function dispatchRawNotification(observers: SessionObserver[], ctx: SessionContext, notification: SessionNotification): void {
  for (const obs of observers) {
    try { obs.onRawNotification?.(ctx, notification); } catch {}
  }
}

function dispatchToolCall(observers: SessionObserver[], ctx: SessionContext, event: ToolCallEvent): void {
  for (const obs of observers) {
    try { obs.onToolCall?.(ctx, event); } catch {}
  }
}

export async function runAcpSession(
  options: AgentSessionOptions
): Promise<AcpSessionResult> {
  const {
    cwd,
    task,
    taskId,
    timeout = DEFAULT_TIMEOUT,
    issueId,
    opencodeBinPath,
    model,
  } = options;

  const taskText = task ?? '';
  const sessionStartTime = Date.now();

  const wfObserver = buildWorkflowObserver(options, {
    taskDescription: taskText.slice(0, 200),
    title: options.title,
    stage: options.stage,
  });
  const extraObservers = options.observers ?? [];
  const observers: SessionObserver[] = wfObserver ? [wfObserver, ...extraObservers] : [...extraObservers];

  log.info('Spawning opencode acp subprocess', { cwd, timeout, issueId: issueId?.slice(0, 8), taskId, promptPreview: taskText.slice(0, 100) });
  wfObserver?.writeSessionLog(issueId, 'acp_session_start', { cwd, timeout, issueId: issueId?.slice(0, 8), taskId, promptPreview: taskText.slice(0, 100), timestamp: new Date().toISOString() });

  const acpProcess = new AcpProcess({
    cwd,
    opencodeBinPath,
    onError: (err) => {
      wfObserver?.writeSessionLog(issueId, 'acp_session_process_error', { error: err.message, timestamp: new Date().toISOString() });
    },
    onExit: ({ exitCode, phase }) => {
      wfObserver?.writeSessionLog(issueId, 'acp_session_process_exit', { exitCode, phase, mode: 'single', timestamp: new Date().toISOString() });
    },
  });

  let agentText = '';
  let agentTextTruncated = false;
  let sessionId = '';


  const connection = new ClientSideConnection(
    (_agent) => ({
      sessionUpdate: async (notification: SessionNotification) => {
        try {
          const update = notification.update;
          const eventType = update.sessionUpdate;

          if (eventType === 'agent_thought_chunk') {
          } else if (
            eventType === 'agent_message_chunk' &&
            update.content &&
            'text' in update.content
          ) {
            const textChunk = (update.content as { text: string }).text;
            if (!agentTextTruncated) {
              agentText += textChunk;
              if (agentText.length > MAX_AGENT_TEXT_LENGTH) {
                agentText = truncateAgentText(agentText);
                agentTextTruncated = true;
              }
            }
            const ctx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
            dispatchTextChunk(observers, ctx, textChunk);
          }

          {
            const ctx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
            dispatchSessionEvent(observers, ctx, eventType, update);
          }

          if (eventType === 'tool_call') {
            const toolData = update as Record<string, unknown>;
            const toolCallData = toolData.toolCall as Record<string, unknown> | undefined;
            const toolStatus = (toolCallData?.status as string) ?? '';
            const state = toolStatus === 'completed' ? 'completed' as const : 'started' as const;
            const toolName = (toolCallData?.toolName as string) ?? '';
            const toolCallId = wfObserver
              ? wfObserver.nextToolCallId(sessionId, toolName, state)
              : `${sessionId}-${toolName}-0`;
            const ctx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
            dispatchToolCall(observers, ctx, {
              toolName,
              state,
              toolCallId,
              title: (toolCallData?.title as string) ?? undefined,
              rawInput: state === 'started' ? toolCallData?.input : undefined,
              rawOutput: state === 'completed' ? toolCallData?.output : undefined,
            });
          }
        } catch (err) {
          log.error('sessionUpdate error', { error: err instanceof Error ? err.message : String(err) });
        }
      },
      requestPermission: async (
        params: RequestPermissionRequest
      ): Promise<RequestPermissionResponse> => {
        const allow = params.options.find(
          (o) => o.kind === 'allow_once' || o.kind === 'allow_always'
        );
        if (allow) {
          return { outcome: { outcome: 'selected', optionId: allow.optionId } };
        }
        return { outcome: { outcome: 'cancelled' } };
      },
    }),
    acpProcess.stream
  );

  const timeoutPromise = new Promise<'timeout'>((resolve) =>
    setTimeout(() => resolve('timeout'), timeout)
  );

  try {
    const initResult = await Promise.race([
      connection.initialize({
        protocolVersion: PROTOCOL_VERSION,
        clientInfo: { name: 'mohist', version: '0.1.0' },
      }),
      timeoutPromise,
      acpProcess.spawnFailure,
    ]);

    acpProcess.markInitialized();

    if (initResult === 'timeout') {
      const duration = Date.now() - sessionStartTime;
      log.error('ACP initialize timed out', { timeout, elapsedMs: duration });
      wfObserver?.writeSessionLog(issueId, 'acp_session_timeout', { phase: 'initialize', timeout, duration, timestamp: new Date().toISOString() });
      await acpProcess.cleanup();
      return { text: agentText, success: false, error: `Timed out during initialize` };
    }

    log.info('ACP initialized, creating session');

    const sessionResult = await Promise.race([
      connection.newSession({
        cwd,
        mcpServers: [],
      }),
      timeoutPromise,
    ]);

    if (sessionResult === 'timeout') {
      const duration = Date.now() - sessionStartTime;
      log.error('ACP newSession timed out', { timeout, elapsedMs: duration });
      wfObserver?.writeSessionLog(issueId, 'acp_session_timeout', { phase: 'newSession', timeout, duration, timestamp: new Date().toISOString() });
      await acpProcess.cleanup();
      return { text: agentText, success: false, error: `Timed out during newSession` };
    }

    sessionId = sessionResult.sessionId;
    log.info('ACP session created', { sessionId });

    if (model) {
      try {
        await connection.setSessionConfigOption({
          sessionId,
          configId: 'model',
          value: model,
        });
        log.info('ACP session model set', { sessionId, model });
      } catch (err) {
        log.warn('setSessionConfigOption for model failed, continuing with default', { sessionId, model, error: err instanceof Error ? err.message : String(err) });
      }
    }

    const startCtx = makeCtx(options, sessionId);
    dispatchSessionStart(observers, startCtx);

    const promptResult = await Promise.race([
      connection.prompt({
        sessionId,
        prompt: [{ type: 'text', text: taskText }],
      }),
      timeoutPromise,
      acpProcess.exitFailure,
    ]);

    if (promptResult === 'timeout') {
      const duration = Date.now() - sessionStartTime;
      log.error('ACP prompt timed out', { sessionId, timeout, elapsedMs: duration });
      wfObserver?.writeSessionLog(issueId, 'acp_session_timeout', { phase: 'prompt', sessionId, timeout, duration, timestamp: new Date().toISOString() });
      const failCtx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
      dispatchStateChange(observers, failCtx, 'running', 'failed');
      try {
        await connection.cancel({ sessionId });
      } catch {
      }
      await acpProcess.cleanup();
      return { text: agentText, success: false, error: `Timed out after ${timeout / 1000}s` };
    }

    const completedCtx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
    dispatchStateChange(observers, completedCtx, 'running', 'completed');

    const successDuration = Date.now() - sessionStartTime;
    log.info('ACP session completed successfully', { sessionId, elapsedMs: successDuration });
    wfObserver?.writeSessionLog(issueId, 'acp_session_completed', { sessionId, success: true, duration: successDuration, timestamp: new Date().toISOString() });
    return { text: agentText, success: true, acpSessionId: sessionId };
  } catch (err) {
    const failDuration = Date.now() - sessionStartTime;
    log.error('ACP session failed', { sessionId, elapsedMs: failDuration, error: err instanceof Error ? err.message : String(err) });
    wfObserver?.writeSessionLog(issueId, 'acp_session_completed', { sessionId, success: false, duration: failDuration, error: err instanceof Error ? err.message : String(err), timestamp: new Date().toISOString() });
    const failCtx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
    dispatchStateChange(observers, failCtx, 'running', 'failed');
    await acpProcess.cleanup();
    const message = err instanceof Error ? err.message : String(err);
    return { text: agentText, success: false, error: message };
  } finally {
    acpProcess.ensureKill();
  }
}

export interface AcpConnection {
  prompt(text: string): Promise<AcpSessionResult>;
  close(): Promise<void>;
}

function createTimeout(ms: number): Promise<'timeout'> {
  return new Promise<'timeout'>((resolve) =>
    setTimeout(() => resolve('timeout'), ms)
  );
}

export async function createAcpConnection(
  options: AgentSessionOptions
): Promise<AcpConnection> {
  const {
    cwd,
    timeout = PER_ROUND_TIMEOUT,
    issueId,
    executionId,
    issueNumber,
    onSessionUpdate,
    opencodeBinPath,
    signal,
    model,
    stage,
    title,
  } = options;

  let agentText = '';
  let agentTextTruncated = false;
  let roundStartIndex = 0;
  let sessionId = '';
  let closed = false;
  const connectionStartTime = Date.now();

  const wfObserver = buildWorkflowObserver(options, {
    taskDescription: 'multi-round acp connection',
    title,
    stage,
  });
  const extraObservers: SessionObserver[] = [];
  if (onSessionUpdate) {
    extraObservers.push(new RawNotificationBridge(onSessionUpdate));
  }
  extraObservers.push(...(options.observers ?? []));
  const observers: SessionObserver[] = wfObserver ? [wfObserver, ...extraObservers] : [...extraObservers];

  log.info('Spawning opencode acp subprocess for multi-round connection', {
    cwd,
    timeout,
    issueId: issueId?.slice(0, 8),
  });
  wfObserver?.writeSessionLog(issueId, 'acp_session_start', { cwd, timeout, issueId: issueId?.slice(0, 8), mode: 'multi-round', timestamp: new Date().toISOString() });

  const acpProcess = new AcpProcess({
    cwd,
    opencodeBinPath,
    onError: (err) => {
      wfObserver?.writeSessionLog(issueId, 'acp_session_process_error', { error: err.message, mode: 'multi-round', timestamp: new Date().toISOString() });
    },
    onExit: ({ exitCode, phase }) => {
      wfObserver?.writeSessionLog(issueId, 'acp_session_process_exit', { exitCode, phase, mode: 'multi-round', timestamp: new Date().toISOString() });
    },
  });


  const connection = new ClientSideConnection(
    (_agent) => ({
      sessionUpdate: async (notification: SessionNotification) => {
        try {
          const update = notification.update;
          const eventType = update.sessionUpdate;

          if (eventType === 'agent_thought_chunk') {
          } else if (
            eventType === 'agent_message_chunk' &&
            update.content &&
            'text' in update.content
          ) {
            const textChunk = (update.content as { text: string }).text;
            if (!agentTextTruncated) {
              agentText += textChunk;
              if (agentText.length > MAX_AGENT_TEXT_LENGTH) {
                agentText = truncateAgentText(agentText);
                agentTextTruncated = true;
              }
            }
            const ctx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
            if (onSessionUpdate) {
              dispatchRawNotification(observers, ctx, notification);
            } else {
              dispatchTextChunk(observers, ctx, textChunk);
            }
          } else if (onSessionUpdate) {
            const ctx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
            dispatchRawNotification(observers, ctx, notification);
          }

          {
            const ctx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
            dispatchSessionEvent(observers, ctx, eventType, update);
          }

          if (!onSessionUpdate && eventType === 'tool_call') {
            const toolData = update as Record<string, unknown>;
            const toolCallData = toolData.toolCall as Record<string, unknown> | undefined;
            const toolStatus = (toolCallData?.status as string) ?? '';
            const state = toolStatus === 'completed' ? 'completed' as const : 'started' as const;
            const toolName = (toolCallData?.toolName as string) ?? '';
            const toolCallId = wfObserver
              ? wfObserver.nextToolCallId(sessionId, toolName, state)
              : `${sessionId}-${toolName}-0`;
            const ctx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
            dispatchToolCall(observers, ctx, {
              toolName,
              state,
              toolCallId,
              title: (toolCallData?.title as string) ?? undefined,
              rawInput: state === 'started' ? toolCallData?.input : undefined,
              rawOutput: state === 'completed' ? toolCallData?.output : undefined,
            });
          }
        } catch (err) {
          log.error('sessionUpdate error', {
            error: err instanceof Error ? err.message : String(err),
          });
        }
      },
      requestPermission: async (
        params: RequestPermissionRequest
      ): Promise<RequestPermissionResponse> => {
        const allow = params.options.find(
          (o) => o.kind === 'allow_once' || o.kind === 'allow_always'
        );
        if (allow) {
          return {
            outcome: { outcome: 'selected', optionId: allow.optionId },
          };
        }
        return { outcome: { outcome: 'cancelled' } };
      },
    }),
    acpProcess.stream
  );

  const initResult = await Promise.race([
    connection.initialize({
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
    const duration = Date.now() - connectionStartTime;
    log.error('ACP initialize timed out', { timeout, elapsedMs: duration });
    wfObserver?.writeSessionLog(issueId, 'acp_session_timeout', { phase: 'initialize', timeout, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
    await acpProcess.cleanup();
    throw new Error('Timed out during initialize');
  }

  log.info('ACP initialized, creating session');

  const sessionResult = await Promise.race([
    connection.newSession({ cwd, mcpServers: [] }),
    createTimeout(timeout),
  ]);

  if (sessionResult === 'timeout') {
    const duration = Date.now() - connectionStartTime;
    log.error('ACP newSession timed out', { timeout, elapsedMs: duration });
    wfObserver?.writeSessionLog(issueId, 'acp_session_timeout', { phase: 'newSession', timeout, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
    await acpProcess.cleanup();
    throw new Error('Timed out during newSession');
  }

  sessionId = sessionResult.sessionId;
  log.info('ACP session created', { sessionId });

  if (model) {
    try {
      await connection.setSessionConfigOption({
        sessionId,
        configId: 'model',
        value: model,
      });
      log.info('ACP connection model set', { sessionId, model });
    } catch (err) {
      log.warn('setSessionConfigOption for model failed, continuing with default', { sessionId, model, error: err instanceof Error ? err.message : String(err) });
    }
  }

  const startCtx = makeCtx(options, sessionId);
  dispatchSessionStart(observers, startCtx);

  return {
    async prompt(text: string): Promise<AcpSessionResult> {
      if (closed) {
        return { text: '', success: false, error: 'Connection is closed' };
      }

      roundStartIndex = agentText.length;

      const abortPromise = signal
        ? new Promise<'aborted'>((resolve) => {
            if (signal.aborted) {
              resolve('aborted');
              return;
            }
            const onAbort = () => resolve('aborted');
            signal.addEventListener('abort', onAbort, { once: true });
          })
        : new Promise<'aborted'>(() => {});

      const promptResult = await Promise.race([
        connection.prompt({
          sessionId,
          prompt: [{ type: 'text', text }],
        }),
        createTimeout(timeout),
        abortPromise,
        acpProcess.exitFailure,
      ]);

      if (promptResult === 'aborted') {
        log.info('ACP prompt aborted by signal', { sessionId });
        wfObserver?.writeSessionLog(issueId, 'acp_session_aborted', { sessionId, mode: 'multi-round', timestamp: new Date().toISOString() });
        try {
          await connection.cancel({ sessionId });
        } catch {
        }
        await acpProcess.cleanup();
        closed = true;
        return {
          text: agentText.slice(roundStartIndex),
          success: false,
          error: 'Agent stopped by user',
          acpSessionId: sessionId,
        };
      }

      if (promptResult === 'timeout') {
        const duration = Date.now() - connectionStartTime;
        log.error('ACP prompt timed out', { sessionId, timeout, elapsedMs: duration });
        wfObserver?.writeSessionLog(issueId, 'acp_session_timeout', { phase: 'prompt', sessionId, timeout, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
        const failCtx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
        dispatchStateChange(observers, failCtx, 'running', 'failed');
        try {
          await connection.cancel({ sessionId });
        } catch {
        }
        return {
          text: agentText.slice(roundStartIndex),
          success: false,
          error: `Timed out after ${timeout / 1000}s`,
          acpSessionId: sessionId,
        };
      }

      return {
        text: agentText.slice(roundStartIndex),
        success: true,
        acpSessionId: sessionId,
      };
    },

    async close(): Promise<void> {
      if (closed) return;
      closed = true;
      const duration = Date.now() - connectionStartTime;
      log.info('ACP connection closed', { sessionId, elapsedMs: duration });
      wfObserver?.writeSessionLog(issueId, 'acp_session_completed', { sessionId, success: true, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
      const closeCtx = makeCtx(options, sessionId, wfObserver?.coderSessionId);
      dispatchStateChange(observers, closeCtx, 'running', 'completed');
      await acpProcess.cleanup();
    },
  };
}
