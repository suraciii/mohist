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

const log = Log.create({ service: 'session' });

export interface AcpSessionOptions {
  cwd: string;
  task: string;
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  workflowLogRepo?: WorkflowLogRepo;
  eventBus?: EventBus;
  throttleMs?: number;
  coderSessionRepo?: CoderSessionRepo;
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
    timeout = DEFAULT_TIMEOUT,
    issueId,
    projectId,
    executionId,
    workflowLogRepo,
    eventBus,
    throttleMs = 100,
    coderSessionRepo,
  } = options;

  let lastTextChunkTime = 0;

  log.info('Spawning opencode acp subprocess', { cwd, timeout, issueId: issueId?.slice(0, 8), taskId: task.slice(0, 100) });

  const proc = spawn('opencode', ['acp'], {
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

  proc.on('error', (err) => {
    log.error('opencode acp subprocess error', { error: err.message });
  });

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

          if (
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
                  issueId: issueId ?? '',
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
              issueId: issueId ?? '',
              projectId: projectId ?? '',
              executionId,
              acpSessionId: sessionId,
              toolName,
              state,
              toolCallId,
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
    ]);

    if (initResult === 'timeout') {
      log.error('ACP initialize timed out', { timeout });
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
      log.error('ACP newSession timed out', { timeout });
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
      log.error('ACP prompt timed out', { sessionId, timeout });
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

    return { text: agentText, success: true, acpSessionId: sessionId };
  } catch (err) {
    log.error('ACP session failed', { sessionId, error: err instanceof Error ? err.message : String(err) });
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
  } = options;

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

  log.info('Spawning opencode acp subprocess for multi-round connection', {
    cwd,
    timeout,
    issueId: issueId?.slice(0, 8),
  });

  const proc = spawn('opencode', ['acp'], {
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

  proc.on('error', (err) => {
    log.error('opencode acp subprocess error', { error: err.message });
  });

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

          if (
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
                  issueId: issueId ?? '',
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
              update as unknown as Record<string, unknown>
            );
          }

          if (eventType === 'tool_call' && eventBus && executionId) {
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
              issueId: issueId ?? '',
              projectId: projectId ?? '',
              executionId,
              acpSessionId: sessionId,
              toolName,
              state,
              toolCallId,
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
  ]);

  if (initResult === 'timeout') {
    log.error('ACP initialize timed out', { timeout });
    await cleanup();
    throw new Error('Timed out during initialize');
  }

  log.info('ACP initialized, creating session');

  const sessionResult = await Promise.race([
    connection.newSession({ cwd, mcpServers: [] }),
    createTimeout(timeout),
  ]);

  if (sessionResult === 'timeout') {
    log.error('ACP newSession timed out', { timeout });
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

      const promptResult = await Promise.race([
        connection.prompt({
          sessionId,
          prompt: [{ type: 'text', text }],
        }),
        createTimeout(timeout),
      ]);

      if (promptResult === 'timeout') {
        log.error('ACP prompt timed out', { sessionId, timeout });
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