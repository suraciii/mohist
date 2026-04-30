import { spawn } from 'child_process';
import { Writable, Readable } from 'stream';
import {
  ClientSideConnection,
  ndJsonStream,
  PROTOCOL_VERSION,
} from '@agentclientprotocol/sdk';
import type {
  SessionNotification,
  RequestPermissionRequest,
  RequestPermissionResponse,
} from '@agentclientprotocol/sdk';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { EventBus } from '../services/event-bus';
import { Log } from '../util/log';
import { resolveOpencodeBinPath } from '../config/config-loader';

const log = Log.create({ service: 'acp-session' });

function writeSessionLog(
  workflowLogRepo: WorkflowLogRepo | undefined,
  issueId: string | undefined,
  eventType: string,
  data: Record<string, unknown>
): void {
  if (!workflowLogRepo || !issueId) return;
  try {
    workflowLogRepo.insert(issueId, null, eventType, data);
  } catch (e) {
    log.warn('workflowLogRepo.insert failed', { eventType, issueId, error: e instanceof Error ? e.message : String(e) });
  }
}

export interface AcpSessionOptions {
  cwd: string;
  task: string;
  taskId?: string;
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  workflowLogRepo?: WorkflowLogRepo;
  eventBus?: EventBus;
  throttleMs?: number;
  coderSessionRepo?: CoderSessionRepo;
  issueNumber?: number;
  onSessionUpdate?: (notification: SessionNotification) => void;
  opencodeBinPath?: string;
}

export interface AcpSessionResult {
  text: string;
  success: boolean;
  error?: string;
  acpSessionId?: string;
}

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

function killProc(proc: import('child_process').ChildProcess): void {
  try {
    proc.kill('SIGTERM');
  } catch {
    // already exited
  }
}

export async function runAcpSession(
  options: AcpSessionOptions
): Promise<AcpSessionResult> {
  const {
    cwd,
    task,
    taskId,
    timeout = DEFAULT_TIMEOUT,
    issueId,
    projectId,
    executionId,
    workflowLogRepo,
    eventBus,
    throttleMs = 100,
    coderSessionRepo,
    issueNumber,
    opencodeBinPath,
  } = options;

  const sseIssueId = String(issueNumber ?? issueId ?? '');
  let lastTextChunkTime = 0;
  const sessionStartTime = Date.now();

  log.info('Spawning opencode acp subprocess', { cwd, timeout, issueId: issueId?.slice(0, 8), taskId, promptPreview: task.slice(0, 100) });
  writeSessionLog(workflowLogRepo, issueId, 'acp_session_start', { cwd, timeout, issueId: issueId?.slice(0, 8), taskId, promptPreview: task.slice(0, 100), timestamp: new Date().toISOString() });

  const resolvedBinPath = opencodeBinPath || resolveOpencodeBinPath() || 'opencode';

  const proc = spawn(resolvedBinPath, ['acp'], {
    cwd,
    stdio: ['pipe', 'pipe', 'inherit'],
    env: Object.fromEntries(
      Object.entries(process.env).filter(
        ([key]) =>
          key !== 'OPENCODE_SERVER_PASSWORD' &&
          key !== 'OPENCODE_SERVER_USERNAME'
      )
    ),
  });

  let initialized = false;
  let rejectOnSpawn: ((err: Error) => void) | undefined;
  const spawnFailure = new Promise<never>((_, reject) => {
    rejectOnSpawn = reject;
  });

  proc.on('error', (err) => {
    log.error('opencode acp subprocess error', { error: err.message });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_process_error', { error: err.message, timestamp: new Date().toISOString() });
    if (!initialized && rejectOnSpawn) {
      rejectOnSpawn(new Error(`[SPAWN_FAILED] ${err.message}`));
    }
  });

  proc.on('exit', () => {
    try { proc.stdin.destroy(); } catch {}
    try { proc.stdout.destroy(); } catch {}
  });

  proc.on('exit', (code) => {
    const phase = initialized ? 'running' : 'init';
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_process_exit', { exitCode: code, phase, mode: 'single', timestamp: new Date().toISOString() });
    if (!initialized && code !== 0) {
      log.error('opencode acp subprocess exited before initialize', { exitCode: code });
      if (rejectOnSpawn) {
        rejectOnSpawn(new Error(`[SPAWN_FAILED] opencode process exited before initialize (exit code: ${code ?? 'signal'})`));
      }
    }
  });

  // Close streams immediately on spawn failure to prevent EPIPE errors
  proc.stdin.on('error', () => {});
  proc.stdout.on('error', () => {});

  let procExited = false;
  let agentText = '';
  let agentTextTruncated = false;
  let sessionId = '';
  let coderToolCallCounter = 0;
  const coderToolCallIds = new Map<string, string[]>();

  const ensureKill = () => {
    if (!procExited) {
      procExited = true;
      killProc(proc);
      setTimeout(() => {
        try {
          proc.kill('SIGKILL');
        } catch {
          // already exited
        }
      }, 5000);
    }
  };

  const input = Writable.toWeb(proc.stdin) as WritableStream<Uint8Array>;
  const output = Readable.toWeb(proc.stdout) as ReadableStream<Uint8Array>;
  const stream = ndJsonStream(input, output);

  const cleanup = async () => {
    const results = await Promise.allSettled([
      stream.readable.cancel().catch(() => {}),
      stream.writable.abort().catch(() => {}),
    ]);
    results.forEach((result, index) => {
      if (result.status === 'rejected') {
        log.error('Cleanup failed', { index, reason: String(result.reason) });
      }
    });
    ensureKill();
  };

  const connection = new ClientSideConnection(
    (_agent) => ({
      sessionUpdate: async (notification: SessionNotification) => {
        try {
          const update = notification.update;
          const eventType = update.sessionUpdate;

          if (eventType === 'agent_thought_chunk') {
            // excluded from agentText — only agent_message_chunk contributes
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
            if (eventBus && executionId) {
              const now = Date.now();
              if (throttleMs === 0 || now - lastTextChunkTime >= throttleMs) {
                eventBus.emit('coder_text_chunk', {
                  issueId: sseIssueId,
                  projectId: projectId ?? '',
                  executionId,
                  acpSessionId: sessionId,
                  text: textChunk,
                });
                lastTextChunkTime = now;
              }
            }
          }

          if (workflowLogRepo) {
            workflowLogRepo.insert(
              issueId ?? '',
              sessionId || null,
              eventType,
              update as unknown as Record<string, unknown>,
            );
          }

          if (
            eventType === 'tool_call' &&
            eventBus &&
            executionId
          ) {
            const toolData = update as Record<string, unknown>;
            const toolCallData = toolData.toolCall as Record<string, unknown> | undefined;
            const toolStatus = (toolCallData?.status as string) ?? '';
            const state = toolStatus === 'completed' ? 'completed' : 'started';
            const toolName = (toolCallData?.toolName as string) ?? '';
            let toolCallId: string;
            if (state === 'started') {
              toolCallId = `${sessionId}-${toolName}-${coderToolCallCounter++}`;
              const key = `${sessionId}-${toolName}`;
              const list = coderToolCallIds.get(key) ?? [];
              list.push(toolCallId);
              coderToolCallIds.set(key, list);
            } else {
              const key = `${sessionId}-${toolName}`;
              const list = coderToolCallIds.get(key) ?? [];
              toolCallId = list.shift() ?? `${sessionId}-${toolName}-${coderToolCallCounter++}`;
              if (list.length > 0) {
                coderToolCallIds.set(key, list);
              } else {
                coderToolCallIds.delete(key);
              }
            }
            eventBus.emit('coder_tool_call', {
              issueId: sseIssueId,
              projectId: projectId ?? '',
              executionId,
              acpSessionId: sessionId,
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
    stream
  );

  const timeoutPromise = new Promise<'timeout'>((resolve) =>
    setTimeout(() => resolve('timeout'), timeout)
  );

  let coderSessionId: string | undefined;

  try {
    const initResult = await Promise.race([
      connection.initialize({
        protocolVersion: PROTOCOL_VERSION,
        clientInfo: { name: 'mohist', version: '0.1.0' },
      }),
      timeoutPromise,
      spawnFailure,
    ]);

    initialized = true;
    rejectOnSpawn = undefined;

    if (initResult === 'timeout') {
      const duration = Date.now() - sessionStartTime;
      log.error('ACP initialize timed out', { timeout, elapsedMs: duration });
      writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'initialize', timeout, duration, timestamp: new Date().toISOString() });
      await cleanup();
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
      writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'newSession', timeout, duration, timestamp: new Date().toISOString() });
      await cleanup();
      return { text: agentText, success: false, error: `Timed out during newSession` };
    }

    sessionId = sessionResult.sessionId;
    log.info('ACP session created', { sessionId });

    if (coderSessionRepo && issueId) {
      try {
        const coderSession = coderSessionRepo.insert({
          issueId,
          acpSessionId: sessionId,
          executionId,
          taskDescription: task.slice(0, 200),
        });
        coderSessionId = coderSession.id;
        log.info('coder_session row created', { coderSessionId, acpSessionId: sessionId });
      } catch (err) {
        log.error('Failed to create coder_session row', { error: err instanceof Error ? err.message : String(err) });
      }
    }

    const promptResult = await Promise.race([
      connection.prompt({
        sessionId,
        prompt: [{ type: 'text', text: task }],
      }),
      timeoutPromise,
    ]);

    if (promptResult === 'timeout') {
      const duration = Date.now() - sessionStartTime;
      log.error('ACP prompt timed out', { sessionId, timeout, elapsedMs: duration });
      writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'prompt', sessionId, timeout, duration, timestamp: new Date().toISOString() });
      if (coderSessionRepo && coderSessionId) {
        try {
          coderSessionRepo.updateStatus(coderSessionId, 'failed');
        } catch (err) {
          log.error('Failed to update coder_session status', { error: err instanceof Error ? err.message : String(err) });
        }
      }
      try {
        await connection.cancel({ sessionId });
      } catch {
        // cancel may fail if session already ended
      }
      await cleanup();
      return { text: agentText, success: false, error: `Timed out after ${timeout / 1000}s` };
    }

    if (coderSessionRepo && coderSessionId) {
      try {
        coderSessionRepo.updateStatus(coderSessionId, 'completed');
      } catch (err) {
        log.error('Failed to update coder_session status to completed', { error: err instanceof Error ? err.message : String(err) });
      }
    }

    const successDuration = Date.now() - sessionStartTime;
    log.info('ACP session completed successfully', { sessionId, elapsedMs: successDuration });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_completed', { sessionId, success: true, duration: successDuration, timestamp: new Date().toISOString() });
    return { text: agentText, success: true, acpSessionId: sessionId };
  } catch (err) {
    const failDuration = Date.now() - sessionStartTime;
    log.error('ACP session failed', { sessionId, elapsedMs: failDuration, error: err instanceof Error ? err.message : String(err) });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_completed', { sessionId, success: false, duration: failDuration, error: err instanceof Error ? err.message : String(err), timestamp: new Date().toISOString() });
    if (coderSessionRepo && coderSessionId) {
      try {
        coderSessionRepo.updateStatus(coderSessionId, 'failed');
      } catch (updateErr) {
        log.error('Failed to update coder_session status', { error: updateErr instanceof Error ? updateErr.message : String(updateErr) });
      }
    }
    await cleanup();
    const message = err instanceof Error ? err.message : String(err);
    return { text: agentText, success: false, error: message };
  } finally {
    ensureKill();
  }
}

export interface AcpConnectionOptions {
  cwd: string;
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  workflowLogRepo?: WorkflowLogRepo;
  eventBus?: EventBus;
  throttleMs?: number;
  coderSessionRepo?: CoderSessionRepo;
  issueNumber?: number;
  onSessionUpdate?: (notification: SessionNotification) => void;
  opencodeBinPath?: string;
  signal?: AbortSignal;
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
  options: AcpConnectionOptions
): Promise<AcpConnection> {
  const {
    cwd,
    timeout = PER_ROUND_TIMEOUT,
    issueId,
    projectId,
    executionId,
    workflowLogRepo,
    eventBus,
    throttleMs = 100,
    coderSessionRepo,
    issueNumber,
    onSessionUpdate,
    opencodeBinPath,
    signal,
  } = options;

  const sseIssueId = String(issueNumber ?? issueId ?? '');
  let lastTextChunkTime = 0;
  let procExited = false;
  let agentText = '';
  let agentTextTruncated = false;
  let roundStartIndex = 0;
  let coderToolCallCounter = 0;
  const coderToolCallIds = new Map<string, string[]>();
  let coderSessionId: string | undefined;
  let sessionId = '';
  let closed = false;
  let initialized = false;
  const connectionStartTime = Date.now();

  log.info('Spawning opencode acp subprocess for multi-round connection', {
    cwd,
    timeout,
    issueId: issueId?.slice(0, 8),
  });
  writeSessionLog(workflowLogRepo, issueId, 'acp_session_start', { cwd, timeout, issueId: issueId?.slice(0, 8), mode: 'multi-round', timestamp: new Date().toISOString() });

  const resolvedBinPath = opencodeBinPath || resolveOpencodeBinPath() || 'opencode';

  const proc = spawn(resolvedBinPath, ['acp'], {
    cwd,
    stdio: ['pipe', 'pipe', 'inherit'],
    env: Object.fromEntries(
      Object.entries(process.env).filter(
        ([key]) =>
          key !== 'OPENCODE_SERVER_PASSWORD' &&
          key !== 'OPENCODE_SERVER_USERNAME'
      )
    ),
  });

  let rejectOnInit: ((err: Error) => void) | undefined;
  const spawnFailure = new Promise<never>((_, reject) => {
    rejectOnInit = reject;
  });

  proc.on('error', (err) => {
    log.error('opencode acp subprocess error', { error: err.message });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_process_error', { error: err.message, mode: 'multi-round', timestamp: new Date().toISOString() });
    if (!initialized && rejectOnInit) {
      rejectOnInit(new Error(`[SPAWN_FAILED] ${err.message}`));
    }
  });

  proc.on('exit', () => {
    try { proc.stdin.destroy(); } catch {}
    try { proc.stdout.destroy(); } catch {}
  });

  proc.on('exit', (code) => {
    const phase = initialized ? 'running' : 'init';
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_process_exit', { exitCode: code, phase, mode: 'multi-round', timestamp: new Date().toISOString() });
    if (!initialized && code !== 0) {
      log.error('opencode acp subprocess exited before initialize', { exitCode: code, mode: 'multi-round' });
      if (rejectOnInit) {
        rejectOnInit(new Error(`[SPAWN_FAILED] opencode process exited before initialize (exit code: ${code ?? 'signal'})`));
      }
    }
  });

  // Close streams immediately on spawn failure to prevent EPIPE errors
  proc.stdin.on('error', () => {});
  proc.stdout.on('error', () => {});

  const ensureKill = () => {
    if (!procExited) {
      procExited = true;
      killProc(proc);
      setTimeout(() => {
        try {
          proc.kill('SIGKILL');
        } catch {
          // already exited
        }
      }, 5000);
    }
  };

  const input = Writable.toWeb(proc.stdin) as WritableStream<Uint8Array>;
  const output = Readable.toWeb(proc.stdout) as ReadableStream<Uint8Array>;
  const stream = ndJsonStream(input, output);

  const cleanup = async () => {
    const results = await Promise.allSettled([
      stream.readable.cancel().catch(() => {}),
      stream.writable.abort().catch(() => {}),
    ]);
    results.forEach((result, index) => {
      if (result.status === 'rejected') {
        log.error('Cleanup failed', { index, reason: String(result.reason) });
      }
    });
    ensureKill();
  };

  const connection = new ClientSideConnection(
    (_agent) => ({
      sessionUpdate: async (notification: SessionNotification) => {
        try {
          const update = notification.update;
          const eventType = update.sessionUpdate;

          if (eventType === 'agent_thought_chunk') {
            // excluded from agentText — only agent_message_chunk contributes
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
            if (onSessionUpdate) {
              onSessionUpdate(notification);
            } else if (eventBus && executionId) {
              const now = Date.now();
              if (throttleMs === 0 || now - lastTextChunkTime >= throttleMs) {
                eventBus.emit('coder_text_chunk', {
                  issueId: sseIssueId,
                  projectId: projectId ?? '',
                  executionId,
                  acpSessionId: sessionId,
                  text: textChunk,
                });
                lastTextChunkTime = now;
              }
            }
          } else if (onSessionUpdate) {
            onSessionUpdate(notification);
          }

          if (workflowLogRepo) {
            workflowLogRepo.insert(
              issueId ?? '',
              sessionId || null,
              eventType,
              update as unknown as Record<string, unknown>
            );
          }

          if (!onSessionUpdate && eventType === 'tool_call' && eventBus && executionId) {
            const toolData = update as Record<string, unknown>;
            const toolCallData = toolData.toolCall as
              | Record<string, unknown>
              | undefined;
            const toolStatus = (toolCallData?.status as string) ?? '';
            const state = toolStatus === 'completed' ? 'completed' : 'started';
            const toolName = (toolCallData?.toolName as string) ?? '';
            let toolCallId: string;
            if (state === 'started') {
              toolCallId = `${sessionId}-${toolName}-${coderToolCallCounter++}`;
              const key = `${sessionId}-${toolName}`;
              const list = coderToolCallIds.get(key) ?? [];
              list.push(toolCallId);
              coderToolCallIds.set(key, list);
            } else {
              const key = `${sessionId}-${toolName}`;
              const list = coderToolCallIds.get(key) ?? [];
              toolCallId =
                list.shift() ??
                `${sessionId}-${toolName}-${coderToolCallCounter++}`;
              if (list.length > 0) {
                coderToolCallIds.set(key, list);
              } else {
                coderToolCallIds.delete(key);
              }
            }
            eventBus.emit('coder_tool_call', {
              issueId: sseIssueId,
              projectId: projectId ?? '',
              executionId,
              acpSessionId: sessionId,
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
    stream
  );

  const initResult = await Promise.race([
    connection.initialize({
      protocolVersion: PROTOCOL_VERSION,
      clientInfo: { name: 'mohist', version: '0.1.0' },
    }),
    createTimeout(timeout),
    spawnFailure,
  ]).catch(async (err: unknown) => {
    await cleanup();
    throw err;
  });

  initialized = true;
  rejectOnInit = undefined;

  if (initResult === 'timeout') {
    const duration = Date.now() - connectionStartTime;
    log.error('ACP initialize timed out', { timeout, elapsedMs: duration });
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'initialize', timeout, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
    await cleanup();
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
    writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'newSession', timeout, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
    await cleanup();
    throw new Error('Timed out during newSession');
  }

  sessionId = sessionResult.sessionId;
  log.info('ACP session created', { sessionId });

  if (coderSessionRepo && issueId) {
    try {
      const coderSession = coderSessionRepo.insert({
        issueId,
        acpSessionId: sessionId,
        executionId,
        taskDescription: 'multi-round acp connection',
      });
      coderSessionId = coderSession.id;
      log.info('coder_session row created', {
        coderSessionId,
        acpSessionId: sessionId,
      });
    } catch (err) {
      log.error('Failed to create coder_session row', {
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

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
      ]);

      if (promptResult === 'aborted') {
        log.info('ACP prompt aborted by signal', { sessionId });
        writeSessionLog(workflowLogRepo, issueId, 'acp_session_aborted', { sessionId, mode: 'multi-round', timestamp: new Date().toISOString() });
        try {
          await connection.cancel({ sessionId });
        } catch {
          // cancel may fail
        }
        await cleanup();
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
        writeSessionLog(workflowLogRepo, issueId, 'acp_session_timeout', { phase: 'prompt', sessionId, timeout, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
        if (coderSessionRepo && coderSessionId) {
          try {
            coderSessionRepo.updateStatus(coderSessionId, 'failed');
          } catch {
            // ignore
          }
        }
        try {
          await connection.cancel({ sessionId });
        } catch {
          // cancel may fail
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
      writeSessionLog(workflowLogRepo, issueId, 'acp_session_completed', { sessionId, success: true, duration, mode: 'multi-round', timestamp: new Date().toISOString() });
      if (coderSessionRepo && coderSessionId) {
        try {
          coderSessionRepo.updateStatus(coderSessionId, 'completed');
        } catch {
          // ignore
        }
      }
      await cleanup();
    },
  };
}