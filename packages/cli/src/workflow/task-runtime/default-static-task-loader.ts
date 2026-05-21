import * as fs from 'fs';
import * as path from 'path';
import { DEFAULT_STAGE_DEFINITIONS } from '../definition/default-workflow';
import type { RuntimeWorkflowDefinitionSnapshot } from '../runner/workflow-runtime-definition';
import { Log } from '../../util/log';
import type { StageContext } from '../stage-context';
import type { ExecutableTask, TaskKind } from './types';
import type { TaskLoader } from './task-loader-registry';

const log = Log.create({ service: 'default-static-task-loader' });

export function createDefaultStaticTaskLoader(worktreePath: string): TaskLoader {
  return {
    kind: 'static',
    load(ctx: StageContext): ExecutableTask[] {
      const definition = ctx.workflowRun?.workflowDefinition
        ? (ctx.workflowRun.workflowDefinition as RuntimeWorkflowDefinitionSnapshot).compiledStageDefinitions.find(candidate => candidate.stage === ctx.issue.stage)
        : DEFAULT_STAGE_DEFINITIONS.find(candidate => candidate.stage === ctx.issue.stage);
      if (!definition) return [];

      const allowedTaskIds = new Set(
        definition.workSources
          ?.filter(source => source.kind === 'static')
          .flatMap(source => source.taskIds ?? [])
          ?? definition.tasks.map(task => task.id),
      );

      return definition.tasks
        .filter(task => allowedTaskIds.has(task.id))
        .map(task => ({
          taskId: task.id,
          title: task.title,
          prompt: resolveWorkflowTaskPrompt(task.with, worktreePath),
          input: task.with,
          kind: toTaskKind(definition.taskExecutionPolicies?.find(policy => policy.taskId === task.id)?.kind ?? 'agent-session'),
        }));
    },
  };
}

function toTaskKind(kind: string): TaskKind {
  if (kind === 'service-call' || kind === 'ralph-task') return kind;
  return 'agent-session';
}

function resolveWorkflowTaskPrompt(input: Record<string, unknown> | undefined, worktreePath: string): string | undefined {
  if (!input) return undefined;
  if (typeof input.prompt === 'string') return input.prompt;
  if (typeof input.promptFile !== 'string') return undefined;

  const promptPath = path.resolve(worktreePath, input.promptFile);
  if (!promptPath.startsWith(path.resolve(worktreePath) + path.sep)) {
    log.warn('Ignoring workflow promptFile outside worktree', { promptFile: input.promptFile });
    return undefined;
  }
  try {
    return fs.readFileSync(promptPath, 'utf-8');
  } catch (err) {
    log.warn('Failed to read workflow promptFile', {
      promptFile: input.promptFile,
      error: err instanceof Error ? err.message : String(err),
    });
    return undefined;
  }
}
