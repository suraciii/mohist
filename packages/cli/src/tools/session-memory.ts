import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance } from '../agent-runtime/tool';

export interface SessionMemoryContext {
  projectPath: string;
}

export interface SessionLearning {
  task_id: string;
  timestamp: string;
  insights: string[];
  adjustments: string[];
  success: boolean;
  execution_summary: string;
  failure_reason?: string;
  failed_attempts?: number;
}

export interface StoreLearningParams {
  change_path: string;
  task_id: string;
  insights: string[];
  adjustments: string[];
  success: boolean;
  execution_summary: string;
  failure_reason?: string;
  failed_attempts?: number;
}

export interface LoadLearningsParams {
  change_path: string;
  format?: 'full' | 'prompt';
}

function getSessionMemoriesDir(changePath: string): string {
  return path.join(changePath, 'session-memories');
}

function formatLearnings(learnings: SessionLearning[]): string {
  if (learnings.length === 0) {
    return 'No previous learnings found.';
  }

  const lines: string[] = [];
  lines.push(`## Previous Task Learnings (${learnings.length} total)`);
  lines.push('');

  for (const learning of learnings) {
    lines.push(`### ${learning.task_id}`);
    lines.push(`- Success: ${learning.success}`);
    if (learning.failure_reason) {
      lines.push(`- Failure Reason: ${learning.failure_reason}`);
    }
    if (learning.failed_attempts) {
      lines.push(`- Failed Attempts: ${learning.failed_attempts}`);
    }
    lines.push(`- Timestamp: ${learning.timestamp}`);
    if (learning.insights.length > 0) {
      lines.push(`- Insights:`);
      for (const insight of learning.insights) {
        lines.push(`  - ${insight}`);
      }
    }
    if (learning.adjustments.length > 0) {
      lines.push(`- Adjustments:`);
      for (const adjustment of learning.adjustments) {
        lines.push(`  - ${adjustment}`);
      }
    }
    lines.push(`- Summary: ${learning.execution_summary}`);
    lines.push('');
  }

  return lines.join('\n');
}

function formatLearningsForPrompt(learnings: SessionLearning[]): string {
  if (learnings.length === 0) {
    return '';
  }

  const lines: string[] = [];
  lines.push('[Previous Task Learnings]');

  for (const learning of learnings) {
    const prefix = `From ${learning.task_id}:`;
    if (!learning.success && learning.failure_reason) {
      lines.push(`${prefix} Failed: "${learning.failure_reason}"`);
      if (learning.adjustments.length > 0) {
        lines.push(`  Adjustments: ${learning.adjustments.join(', ')}`);
      }
    } else {
      lines.push(`${prefix} "${learning.execution_summary}"`);
      if (learning.insights.length > 0) {
        lines.push(`  Insights: ${learning.insights.join(', ')}`);
      }
    }
  }

  return lines.join('\n');
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createStoreLearningTool(context: SessionMemoryContext): ToolInstance<any> {
  return Tool.define('store_learning', {
    description:
      'Store a task execution learning/insight to the session memories. This captures what was learned ' +
      'during task execution (success patterns, failure reasons, adjustments needed) for future tasks to reference.',
    parameters: z
      .object({
        change_path: z
          .string()
          .describe(
            'Path to the Change directory (relative to project root or absolute). ' +
            'The session-memories directory will be created inside this directory.',
          ),
        task_id: z
          .string()
          .describe('The task identifier (e.g., "T-001", "T-002").'),
        insights: z
          .array(z.string())
          .describe('Key insights discovered during task execution (constraints, patterns, patterns).'),
        adjustments: z
          .array(z.string())
          .describe('Suggestions for subsequent tasks based on this execution.'),
        success: z
          .boolean()
          .describe('Whether the task completed successfully.'),
        execution_summary: z
          .string()
          .describe('Brief summary of what was executed or accomplished.'),
        failure_reason: z
          .string()
          .optional()
          .describe('Why the task failed (if success is false).'),
        failed_attempts: z
          .number()
          .optional()
          .describe('Number of attempts before completion/failure.'),
      })
      .strict(),
    execute: async (params) => {
      const resolved = path.resolve(context.projectPath, params.change_path);

      if (
        !resolved.startsWith(context.projectPath + path.sep) &&
        resolved !== context.projectPath
      ) {
        return 'Error: change_path is outside the project directory';
      }

      const memoriesDir = getSessionMemoriesDir(resolved);

      if (
        !memoriesDir.startsWith(resolved + path.sep) &&
        memoriesDir !== resolved
      ) {
        return 'Error: session-memories path escapes the change directory';
      }

      if (!fs.existsSync(memoriesDir)) {
        fs.mkdirSync(memoriesDir, { recursive: true });
      }

      const taskIdSanitized = params.task_id.replace(/[^a-zA-Z0-9_-]/g, '_');
      const filePath = path.join(memoriesDir, `${taskIdSanitized}.json`);

      if (!filePath.startsWith(memoriesDir + path.sep)) {
        return 'Error: task_id creates path outside session-memories directory';
      }

      const learning: SessionLearning = {
        task_id: params.task_id,
        timestamp: new Date().toISOString(),
        insights: params.insights,
        adjustments: params.adjustments,
        success: params.success,
        execution_summary: params.execution_summary,
        failure_reason: params.failure_reason,
        failed_attempts: params.failed_attempts,
      };

      try {
        fs.writeFileSync(filePath, JSON.stringify(learning, null, 2), 'utf-8');
      } catch (err: unknown) {
        const message = err instanceof Error ? err.message : String(err);
        return `Error: failed to write learning file: ${message}`;
      }

      return `Learning stored for ${params.task_id} at ${params.change_path}/session-memories/${taskIdSanitized}.json`;
    },
  });
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createLoadLearningsTool(context: SessionMemoryContext): ToolInstance<any> {
  return Tool.define('load_learnings', {
    description:
      'Load all session learnings from previous tasks for the current Change. ' +
      'Returns formatted learnings that can be included in task context.',
    parameters: z
      .object({
        change_path: z
          .string()
          .describe(
            'Path to the Change directory (relative to project root or absolute).',
          ),
        format: z
          .enum(['full', 'prompt'])
          .optional()
          .default('full')
          .describe(
            'Output format: "full" returns detailed JSON-like format, "prompt" returns concise format suitable for prompts.',
          ),
      })
      .strict(),
    execute: async (params) => {
      const resolved = path.resolve(context.projectPath, params.change_path);

      if (
        !resolved.startsWith(context.projectPath + path.sep) &&
        resolved !== context.projectPath
      ) {
        return 'Error: change_path is outside the project directory';
      }

      const memoriesDir = getSessionMemoriesDir(resolved);

      if (!fs.existsSync(memoriesDir)) {
        if (params.format === 'prompt') {
          return '';
        }
        return formatLearnings([]);
      }

      let files: string[];
      try {
        files = fs.readdirSync(memoriesDir).filter((f) => f.endsWith('.json'));
      } catch (err: unknown) {
        const message = err instanceof Error ? err.message : String(err);
        return `Error: failed to read session-memories directory: ${message}`;
      }

      if (files.length === 0) {
        if (params.format === 'prompt') {
          return '';
        }
        return formatLearnings([]);
      }

      const learnings: SessionLearning[] = [];

      for (const file of files) {
        const filePath = path.join(memoriesDir, file);
        try {
          const content = fs.readFileSync(filePath, 'utf-8');
          const learning = JSON.parse(content) as SessionLearning;
          learnings.push(learning);
        } catch {
          // Skip invalid files
        }
      }

      learnings.sort((a, b) => {
        const numA = parseInt(a.task_id.replace(/[^0-9]/g, ''), 10) || 0;
        const numB = parseInt(b.task_id.replace(/[^0-9]/g, ''), 10) || 0;
        return numA - numB;
      });

      if (params.format === 'prompt') {
        return formatLearningsForPrompt(learnings);
      }

      return formatLearnings(learnings);
    },
  });
}