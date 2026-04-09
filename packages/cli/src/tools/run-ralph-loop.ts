import { z } from 'zod';
import { Tool, type ToolInstance, type ToolRegistry } from '../agent-runtime/tool';
import { detectOpenSpecChange } from '../openspec/detector';
import { RalphExecutor } from '../openspec/ralph-executor';
import { IssueStatus, type Stage } from '../types';
import type { EventBus } from '../services/event-bus';

export interface ToolContext {
  worktreePath: string;
  issueId: string;
  projectId?: string;
  eventBus?: EventBus;
  toolRegistry?: ToolRegistry;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createRunRalphLoopTool(context: ToolContext): ToolInstance<any> {
  return Tool.define('run_ralph_loop', {
    description:
      'Run Ralph task loop for OpenSpec workflow. Detects Change directory for the current issue, ' +
      'and executes tasks from prd.json sequentially using Ralph-style loop. ' +
      'Use this in build stage when OpenSpec Change is detected.',
    parameters: z.object({
      issueNumber: z.number().describe('The issue number to run Ralph loop for'),
    }).strict(),
    execute: async (params: { issueNumber: number }) => {
      const fakeIssue = { 
        id: 'fake', 
        number: params.issueNumber, 
        title: '', 
        body: undefined, 
        stage: 'build' as Stage, 
        status: IssueStatus.Active, 
        projectId: '', 
        labels: [] as string[], 
        createdAt: '', 
        updatedAt: '' 
      };
      const change = detectOpenSpecChange(context.worktreePath, fakeIssue);
      
      if (!change) {
        return 'No OpenSpec Change found for this issue. Use spawn_coder instead.';
      }

      const executionId = context.toolRegistry?.getCurrentExecutionId() ?? undefined;

      const executor = new RalphExecutor({
        worktreePath: context.worktreePath,
        projectPath: context.worktreePath,
        issueId: context.issueId,
        projectId: context.projectId,
        eventBus: context.eventBus,
        executionId,
      });

      const loopResult = await executor.execute(change);
      
      const lines: string[] = [];
      lines.push(`Ralph Loop Complete`);
      lines.push(`Total: ${loopResult.total}, Completed: ${loopResult.completed}, Failed: ${loopResult.failed}`);
      lines.push(`Success: ${loopResult.success}`);
      return lines.join('\n');
    },
  });
}