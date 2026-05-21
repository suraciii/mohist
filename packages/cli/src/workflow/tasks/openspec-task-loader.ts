import type { StageContext } from '../stage-context';
import type { TaskLoader } from './task-loader-registry';
import type { ExecutableTask } from './types';
import * as path from 'path';
import { detectOpenSpecChange } from '../../openspec/detector';
import { readTasks } from '../../openspec/ralph-executor';
import type { RuntimeStageDefinition } from '../runner/workflow-runtime-definition';
import type { WorkflowTasksFromDefinition } from '../model';
import { createWorkflowTemplateContext, renderWorkflowTemplate } from '../template';
import type { Task } from '../../openspec/context-assembler';
import { workflowDefinitionSnapshotFromUnknown } from '../projection/workflow-run-snapshot';

type OpenSpecTaskSourceConfig = {
  path?: string;
  task?: {
    uses?: string;
    with?: Record<string, unknown>;
  };
};

export function createOpenSpecTaskLoader(): TaskLoader {
  return {
    kind: 'openspec',
    load(ctx: StageContext): ExecutableTask[] {
      const change = detectOpenSpecChange(ctx.acpOptions.cwd, ctx.issue);
      if (!change) return [];

      const tasksFrom = resolveOpenSpecTasksFrom(ctx);
      const templateContext = createWorkflowTemplateContext({
        ctx,
        worktreePath: ctx.acpOptions.cwd,
        snapshot: workflowDefinitionSnapshotFromUnknown(ctx.workflowRun?.workflowDefinition),
      });
      const sourceConfig = openSpecTaskSourceConfig(tasksFrom);
      const tasksPath = resolveTasksPath(sourceConfig.path, change.tasksPath, templateContext);
      const tasks = readTasks(tasksPath);
      if (!tasks) return [];

      const taskTemplate = taskTemplateFrom(sourceConfig);

      return tasks.map(task => {
        const withConfig = renderTaskWith(taskTemplate.with, task, templateContext);
        return {
          taskId: task.id,
          title: task.title,
          uses: taskTemplate.uses,
          input: withConfig,
        };
      });
    },
  };
}

function resolveOpenSpecTasksFrom(ctx: StageContext): WorkflowTasksFromDefinition | undefined {
  const snapshot = workflowDefinitionSnapshotFromUnknown(ctx.workflowRun?.workflowDefinition);
  const stageDefinition = (snapshot as { compiledStageDefinitions?: RuntimeStageDefinition[] } | null)?.compiledStageDefinitions
    ?.find(stage => stage.stage === ctx.issue.stage);
  const tasksFrom = stageDefinition?.tasksFrom;
  if (tasksFrom && typeof tasksFrom !== 'string' && tasksFrom.uses === 'mohist/openspec-tasks') return tasksFrom;
  return undefined;
}

function openSpecTaskSourceConfig(tasksFrom: WorkflowTasksFromDefinition | undefined): OpenSpecTaskSourceConfig {
  const withConfig = recordValue(tasksFrom?.with);
  const rawTask = recordValue(withConfig?.task);
  return {
    path: typeof withConfig?.path === 'string' ? withConfig.path : undefined,
    task: rawTask ? {
      uses: typeof rawTask.uses === 'string' ? rawTask.uses : undefined,
      with: recordValue(rawTask.with),
    } : undefined,
  };
}

function resolveTasksPath(
  configuredPath: string | undefined,
  fallbackPath: string,
  context: ReturnType<typeof createWorkflowTemplateContext>,
): string {
  if (!configuredPath) return fallbackPath;
  const renderedPath = renderWorkflowTemplate(configuredPath, context);
  return path.isAbsolute(renderedPath) ? renderedPath : path.resolve(context.worktreePath ?? process.cwd(), renderedPath);
}

function taskTemplateFrom(config: OpenSpecTaskSourceConfig): { uses: string; with?: Record<string, unknown> } {
  const rawTask = config.task;
  const uses = typeof rawTask?.uses === 'string' ? rawTask.uses : 'mohist/agent';
  return {
    uses,
    with: recordValue(rawTask?.with),
  };
}

function renderTaskWith(
  withConfig: Record<string, unknown> | undefined,
  task: Task,
  context: ReturnType<typeof createWorkflowTemplateContext>,
): Record<string, unknown> | undefined {
  if (!withConfig) return undefined;
  return renderValue(withConfig, task, context) as Record<string, unknown>;
}

function renderValue(value: unknown, task: Task, context: ReturnType<typeof createWorkflowTemplateContext>): unknown {
  if (typeof value === 'string') return renderOpenSpecTaskTemplate(value, task, context);
  if (Array.isArray(value)) return value.map(item => renderValue(item, task, context));
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, renderValue(item, task, context)]));
  }
  return value;
}

function renderOpenSpecTaskTemplate(template: string, task: Task, context: ReturnType<typeof createWorkflowTemplateContext>): string {
  const taskValues = {
    'task.id': task.id,
    'task.title': task.title,
    'task.description': task.description,
    'task.acceptanceCriteria': formatAcceptanceCriteria(task.acceptanceCriteria),
  };
  const withTaskValues = template.replace(/\{\{\s*(task\.[a-zA-Z0-9_.]+)\s*\}\}/g, (_match, key: keyof typeof taskValues) => taskValues[key] ?? '');
  return renderWorkflowTemplate(withTaskValues, context);
}

function formatAcceptanceCriteria(criteria: string[] | undefined): string {
  return (criteria ?? []).map(item => `- ${item}`).join('\n');
}

function recordValue(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}
