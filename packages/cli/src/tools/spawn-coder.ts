import { spawn } from 'child_process';
import { Writable, Readable } from 'stream';
import { z } from 'zod';
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
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { EventBus } from '../services/event-bus';

const DEFAULT_TIMEOUT = 30 * 60 * 1000;

const TRUNCATE_THRESHOLD = 8000;
const TRUNCATE_HEAD = 3000;
const TRUNCATE_TAIL = 5000;

function maybeTruncate(text: string): string {
  if (text.length <= TRUNCATE_THRESHOLD) return text;
  const head = text.slice(0, TRUNCATE_HEAD);
  const tail = text.slice(-TRUNCATE_TAIL);
  return `${head}\n... [truncated: ${text.length} chars, showing first ${TRUNCATE_HEAD} and last ${TRUNCATE_TAIL}] ...\n${tail}`;
}

function replaceTemplateVariables(
  template: string,
  variables: Record<string, unknown>
): string {
  let result = template;
  for (const [key, value] of Object.entries(variables)) {
    const replacer = (_match: string, nestedPath: string): string => {
      const parts = nestedPath.split('.');
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      let current: any = value;
      for (const part of parts) {
        if (current == null || typeof current !== 'object') return String(value);
        current = (current as Record<string, unknown>)[part];
      }
      return current != null ? String(current) : String(value);
    };
    result = result.replaceAll(
      new RegExp(`\\{${key}\\.(\\w+(?:\\.\\w+)*)\\}`, 'g'),
      replacer
    );
    result = result.replaceAll(`{${key}}`, String(value));
  }
  return result;
}

function killProc(proc: import('child_process').ChildProcess): void {
  try {
    proc.kill('SIGTERM');
  } catch {
    // already exited
  }
}

async function runAcpOneshot(
  cwd: string,
  task: string,
  timeout: number,
  issueId: string,
  workflowLogRepo?: WorkflowLogRepo,
  eventBus?: EventBus,
  projectId: string = '',
): Promise<{ text: string; error?: string }> {
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

  let agentText = '';
  let sessionId = '';

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
            agentText += (update.content as { text: string }).text;
          }

          if (workflowLogRepo) {
            workflowLogRepo.insert(
              issueId,
              sessionId || null,
              eventType,
              update as unknown as Record<string, unknown>,
            );
          }

          if (eventType === 'tool_call' && eventBus) {
            const toolData = update as Record<string, unknown>;
            const toolCallData = toolData.toolCall as Record<string, unknown> | undefined;
            eventBus.emit('tool_call', {
              issueId,
              projectId,
              toolName: (toolCallData?.toolName as string) ?? '',
              status: (toolCallData?.status as string) ?? '',
              locations: toolCallData?.locations as string[] | undefined,
            });
          }
        } catch (err) {
          console.error('[spawn_coder] sessionUpdate error:', err);
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
      ensureKill();
      return { text: agentText, error: `Timed out during initialize` };
    }

    const sessionResult = await Promise.race([
      connection.newSession({
        cwd,
        mcpServers: [],
      }),
      timeoutPromise,
    ]);

    if (sessionResult === 'timeout') {
      ensureKill();
      return { text: agentText, error: `Timed out during newSession` };
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
      ensureKill();
      return { text: agentText, error: `Timed out after ${timeout / 1000}s` };
    }

    return { text: agentText };
  } catch (err) {
    ensureKill();
    const message = err instanceof Error ? err.message : String(err);
    return { text: agentText, error: message };
  } finally {
    ensureKill();
  }
}

export interface SpawnCoderContext {
  worktreePath?: string;
  issueId?: string;
  projectId?: string;
  workflowLogRepo?: WorkflowLogRepo;
  eventBus?: EventBus;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createSpawnCoderTool(
  context?: SpawnCoderContext
): ToolInstance<any> {
  return Tool.define('spawn_coder', {
    description:
      'Spawn an opencode acp oneshot session to execute a coding task. ' +
      'Provide a taskTemplate with optional variable placeholders (e.g. {issue.title}, {plan.output}) ' +
      'and a variables object for replacement. The tool starts opencode acp --cwd <worktree>, ' +
      'initializes ACP, creates a session, sends the prompt, waits for the result, and cleans up.',
    parameters: z.object({
      taskTemplate: z
        .string()
        .describe(
          'Task message template with optional {variable} or {object.field} placeholders'
        ),
      variables: z
        .record(z.string(), z.unknown())
        .optional()
        .default({})
        .describe(
          'Variables for template replacement. Nested access via dot notation, e.g. {issue.title}'
        ),
      cwd: z
        .string()
        .optional()
        .describe('Working directory for the opencode acp subprocess'),
      timeout: z
        .number()
        .optional()
        .describe(
          'Timeout in milliseconds. Defaults to 30 minutes (1800000).'
        ),
    }),
    execute: async (params) => {
      const cwd = params.cwd ?? context?.worktreePath;
      if (!cwd) {
        return 'Error: no working directory specified. Provide cwd parameter or configure default worktreePath.';
      }

      const timeout = params.timeout ?? DEFAULT_TIMEOUT;
      const task = replaceTemplateVariables(
        params.taskTemplate,
        params.variables
      );

      console.log(
        `[spawn_coder] Spawning: opencode acp (cwd=${cwd}, timeout=${timeout}ms)`
      );
      console.log(`[spawn_coder] Task: ${task.slice(0, 200)}${task.length > 200 ? '...' : ''}`);

      const result = await runAcpOneshot(
        cwd,
        task,
        timeout,
        context?.issueId ?? '',
        context?.workflowLogRepo,
        context?.eventBus,
        context?.projectId ?? '',
      );

      if (result.error && !result.text) {
        return `Error: ${result.error}`;
      }

      const text = result.text.trim();
      if (result.error) {
        return `${result.error}\n\nPartial output:\n${maybeTruncate(text) || '(no output)'}`;
      }

      if (!text) {
        return 'Coding agent completed with no output.';
      }

      return maybeTruncate(text);
    },
  });
}
