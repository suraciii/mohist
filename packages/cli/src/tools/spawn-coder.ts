import { z } from 'zod';
import { Tool, type ToolInstance, type ToolRegistry } from '../agent-runtime/tool';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { EventBus } from '../services/event-bus';
import { runAcpSession } from '../agent-runtime/acp-session';

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

export interface SpawnCoderContext {
  worktreePath?: string;
  issueId?: string;
  projectId?: string;
  workflowLogRepo?: WorkflowLogRepo;
  eventBus?: EventBus;
  toolRegistry?: ToolRegistry;
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

      const executionId = context?.toolRegistry?.getCurrentExecutionId() ?? undefined;

      const result = await runAcpSession({
        cwd,
        task,
        timeout,
        issueId: context?.issueId,
        projectId: context?.projectId,
        executionId,
        workflowLogRepo: context?.workflowLogRepo,
        eventBus: context?.eventBus,
      });

      if (!result.success && !result.text) {
        return `Error: ${result.error}`;
      }

      const text = result.text.trim();
      if (!result.success) {
        return `${result.error}\n\nPartial output:\n${maybeTruncate(text) || '(no output)'}`;
      }

      if (!text) {
        return 'Coding agent completed with no output.';
      }

      return maybeTruncate(text);
    },
  });
}
