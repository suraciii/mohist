import * as fs from 'fs';
import * as path from 'path';
import { DEFAULT_STAGE_DEFINITIONS } from '../workflows/mohist-default';
import { workflowDefinitionSnapshotFromUnknown } from '../../projection/workflow-run-snapshot';
import { Log } from '../../../util/log';
import type { StageContext as MohistStageContext } from '../../stage-context';
import type { StageContext } from '@mohist/workflow/runtime';
import type { ExecutableTask } from '../../tasks/types';
import type { TaskLoader } from '../../tasks/task-loader-registry';

const log = Log.create({ service: 'default-static-task-loader' });

export function createDefaultStaticTaskLoader(worktreePath: string): TaskLoader {
  return {
    kind: 'static',
    load(ctx: StageContext): ExecutableTask[] {
      const mohistCtx = ctx as unknown as MohistStageContext;
      const definition = (mohistCtx.workflowRun?.workflowDefinition
        ? workflowDefinitionSnapshotFromUnknown(mohistCtx.workflowRun.workflowDefinition)?.compiledStageDefinitions
        : DEFAULT_STAGE_DEFINITIONS
      )?.find(candidate => candidate.stage === mohistCtx.issue.stage);
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
          uses: task.uses,
          prompt: resolveWorkflowTaskPrompt(task.with, worktreePath),
          input: task.with,
        }));
    },
  };
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
