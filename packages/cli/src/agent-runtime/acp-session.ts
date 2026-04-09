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
import type { EventBus } from '../services/event-bus';

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
}

export interface AcpSessionResult {
  text: string;
  success: boolean;
  error?: string;
  acpSessionId?: string;
}

const DEFAULT_TIMEOUT = 30 * 60 * 1000;
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024;

function truncateAgentText(text: string): string {
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
  } = options;

  let lastTextChunkTime = 0;

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

  let procExited = false;
  let agentText = '';
  let agentTextTruncated = false;
  let sessionId = '';

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
        console.error(`[acp-session] Cleanup ${index} failed:`, result.reason);
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
            eventBus.emit('coder_tool_call', {
              issueId: issueId ?? '',
              projectId: projectId ?? '',
              executionId,
              acpSessionId: sessionId,
              toolName: (toolCallData?.toolName as string) ?? '',
              state,
            });
          }
        } catch (err) {
          console.error('[acp-session] sessionUpdate error:', err);
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

  try {
    const initResult = await Promise.race([
      connection.initialize({
        protocolVersion: PROTOCOL_VERSION,
        clientInfo: { name: 'mohist', version: '0.1.0' },
      }),
      timeoutPromise,
    ]);

    if (initResult === 'timeout') {
      await cleanup();
      return { text: agentText, success: false, error: `Timed out during initialize` };
    }

    const sessionResult = await Promise.race([
      connection.newSession({
        cwd,
        mcpServers: [],
      }),
      timeoutPromise,
    ]);

    if (sessionResult === 'timeout') {
      await cleanup();
      return { text: agentText, success: false, error: `Timed out during newSession` };
    }

    sessionId = sessionResult.sessionId;

    const promptResult = await Promise.race([
      connection.prompt({
        sessionId,
        prompt: [{ type: 'text', text: task }],
      }),
      timeoutPromise,
    ]);

    if (promptResult === 'timeout') {
      try {
        await connection.cancel({ sessionId });
      } catch {
        // cancel may fail if session already ended
      }
      await cleanup();
      return { text: agentText, success: false, error: `Timed out after ${timeout / 1000}s` };
    }

    return { text: agentText, success: true, acpSessionId: sessionId };
  } catch (err) {
    await cleanup();
    const message = err instanceof Error ? err.message : String(err);
    return { text: agentText, success: false, error: message };
  } finally {
    ensureKill();
  }
}