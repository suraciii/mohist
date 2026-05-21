import { getWorkflowUseDefinition } from '../uses-catalog';
import type {
  CompiledStageDefinition,
  TaskDefinition,
  WorkflowTasksFromSource,
  WorkflowDefinitionSnapshot,
} from '../model/workflow-definition';

export type WorkSourceKind = 'static' | 'openspec' | 'runtime';

export interface WorkSourceDefinition {
  kind: WorkSourceKind;
  taskIds?: string[];
}

export interface TaskExecutionPolicy {
  taskId: string;
  workSourceKind?: WorkSourceKind;
  agentSessionRef?: string;
}

export type RuntimeStageDefinition = CompiledStageDefinition & {
  workSources?: WorkSourceDefinition[];
  taskExecutionPolicies?: TaskExecutionPolicy[];
};

export interface RuntimeWorkflowDefinitionSnapshot extends WorkflowDefinitionSnapshot {
  compiledStageDefinitions: RuntimeStageDefinition[];
}

export function compileRuntimeStageDefinitions(stages: CompiledStageDefinition[]): RuntimeStageDefinition[] {
  return stages.map(stage => {
    const runtime: RuntimeStageDefinition = {
      ...stage,
      tasks: stage.tasks.map(cloneTaskDefinition),
      tasksFrom: cloneTasksFrom(stage.tasksFrom),
      checks: stage.checks.map(cloneCheckDefinition),
      on: cloneStageEvents(stage.on),
      workSources: compileWorkSources(stage),
    };

    runtime.taskExecutionPolicies = compileTaskExecutionPolicies(runtime, runtime);
    return runtime;
  });
}

export function compileRuntimeWorkflowDefinitionSnapshot(snapshot: WorkflowDefinitionSnapshot): RuntimeWorkflowDefinitionSnapshot {
  return {
    ...snapshot,
    source: { ...snapshot.source },
    resolvedDefinition: {
      ...snapshot.resolvedDefinition,
      stages: snapshot.resolvedDefinition.stages.map(cloneStageForRuntime),
      defaults: snapshot.resolvedDefinition.defaults ? { ...snapshot.resolvedDefinition.defaults } : undefined,
      artifacts: snapshot.resolvedDefinition.artifacts ? { ...snapshot.resolvedDefinition.artifacts } : undefined,
    },
    compiledStageDefinitions: compileRuntimeStageDefinitions(snapshot.compiledStageDefinitions),
  };
}

export function cloneRuntimeStageDefinition(stage: RuntimeStageDefinition): RuntimeStageDefinition {
  return {
    ...stage,
    tasks: stage.tasks.map(cloneTaskDefinition),
    tasksFrom: cloneTasksFrom(stage.tasksFrom),
    checks: stage.checks.map(cloneCheckDefinition),
    on: cloneStageEvents(stage.on),
    workSources: stage.workSources?.map(source => ({
      ...source,
      taskIds: source.taskIds ? [...source.taskIds] : undefined,
    })),
    taskExecutionPolicies: stage.taskExecutionPolicies?.map(policy => ({ ...policy })),
    checkPolicies: stage.checkPolicies.map(policy => ({ ...policy })),
    approvalPolicy: stage.approvalPolicy ? { ...stage.approvalPolicy } : undefined,
    invalidationPolicy: stage.invalidationPolicy ? {
      entries: stage.invalidationPolicy.entries.map(entry => ({
        ...entry,
        invalidates: {
          tasks: entry.invalidates.tasks ? [...entry.invalidates.tasks] : undefined,
          checks: entry.invalidates.checks ? [...entry.invalidates.checks] : undefined,
          approval: entry.invalidates.approval,
        },
      })),
    } : undefined,
  };
}

function cloneTaskDefinition(task: TaskDefinition): TaskDefinition {
  return {
    ...task,
    with: task.with ? { ...task.with } : undefined,
    onSuccess: task.onSuccess ? {
      emit: task.onSuccess.emit ? [...task.onSuccess.emit] : undefined,
    } : undefined,
    dependsOn: task.dependsOn ? [...task.dependsOn] : undefined,
  };
}

function cloneCheckDefinition(check: CompiledStageDefinition['checks'][number]): CompiledStageDefinition['checks'][number] {
  const cloned = {
    ...check,
    with: check.with ? { ...check.with } : undefined,
  };
  if (!check.onFailure?.retry) return cloned;
  return {
    ...cloned,
    onFailure: {
      retry: {
        limit: check.onFailure.retry.limit,
        task: cloneTaskDefinition(check.onFailure.retry.task),
        inputFrom: check.onFailure.retry.inputFrom?.map(input => ({ ...input })),
      },
    },
  };
}

function cloneStageForRuntime(stage: WorkflowDefinitionSnapshot['resolvedDefinition']['stages'][number]): WorkflowDefinitionSnapshot['resolvedDefinition']['stages'][number] {
  return {
    ...stage,
    tasks: stage.tasks.map(cloneTaskDefinition),
    tasksFrom: cloneTasksFrom(stage.tasksFrom),
    checks: stage.checks.map(cloneCheckDefinition),
    on: cloneStageEvents(stage.on),
  };
}

function cloneTasksFrom(tasksFrom: WorkflowTasksFromSource | undefined): WorkflowTasksFromSource | undefined {
  if (!tasksFrom || typeof tasksFrom === 'string') return tasksFrom;
  return {
    uses: tasksFrom.uses,
    with: tasksFrom.with ? { ...tasksFrom.with } : undefined,
  };
}

function cloneStageEvents(on: CompiledStageDefinition['on']): CompiledStageDefinition['on'] {
  return on ? Object.fromEntries(Object.entries(on).map(([event, policy]) => [event, {
    reset: {
      tasks: policy.reset.tasks ? [...policy.reset.tasks] : undefined,
      checks: Array.isArray(policy.reset.checks) ? [...policy.reset.checks] : policy.reset.checks,
      approval: policy.reset.approval,
    },
  }])) : undefined;
}

function compileWorkSources(stage: CompiledStageDefinition, existingSources?: WorkSourceDefinition[]): WorkSourceDefinition[] | undefined {
  const workSources: WorkSourceDefinition[] = [];
  if (stage.tasks.length > 0) {
    workSources.push({ kind: 'static', taskIds: stage.tasks.map(task => task.id) });
  }
  if (stage.tasksFrom) {
    const sourceKind = getWorkflowUseDefinition(taskSourceUse(stage.tasksFrom))?.sourceKind;
    if (sourceKind) workSources.push({ kind: sourceKind });
  }
  for (const source of existingSources ?? []) {
    if (workSources.some(candidate => candidate.kind === source.kind)) continue;
    workSources.push({
      ...source,
      taskIds: source.taskIds ? [...source.taskIds] : undefined,
    });
  }
  return workSources.length > 0 ? workSources : undefined;
}

function taskSourceUse(tasksFrom: NonNullable<CompiledStageDefinition['tasksFrom']>): string {
  return typeof tasksFrom === 'string' ? tasksFrom : tasksFrom.uses;
}

function taskWorkSourceKind(stage: CompiledStageDefinition, taskId: string, workSources: WorkSourceDefinition[] | undefined): WorkSourceKind | undefined {
  for (const source of workSources ?? []) {
    if (source.kind === 'static') {
      const taskIds = source.taskIds ?? stage.tasks.map(task => task.id);
      if (taskIds.includes(taskId)) return source.kind;
    }
  }
  return undefined;
}

function policyKey(policy: TaskExecutionPolicy): string {
  return `${policy.taskId}:${policy.workSourceKind ?? '*'}`;
}

function compileTaskExecutionPolicies(stage: CompiledStageDefinition, compiled: Partial<RuntimeStageDefinition>): TaskExecutionPolicy[] | undefined {
  const policies = new Map<string, TaskExecutionPolicy>();

  for (const policy of compiled.taskExecutionPolicies ?? []) {
    policies.set(policyKey(policy), { ...policy });
  }

  for (const task of stage.tasks) {
    const workSourceKind = taskWorkSourceKind(stage, task.id, compiled.workSources);
    const existing = compiled.taskExecutionPolicies?.find(policy => policy.taskId === task.id && policy.workSourceKind === workSourceKind);
    if (existing) continue;

    const policy: TaskExecutionPolicy = {
      taskId: task.id,
      workSourceKind,
    };

    if (task.with && typeof task.with.session === 'string') {
      policy.agentSessionRef = task.with.session;
    }

    policies.set(policyKey(policy), policy);
  }

  for (const check of stage.checks) {
    const task = check.onFailure?.retry?.task;
    if (!task) continue;
    const existing = [...policies.values()].some(policy => policy.taskId === task.id && policy.workSourceKind === 'runtime');
    if (existing) continue;
    const policy: TaskExecutionPolicy = {
      taskId: task.id,
      workSourceKind: 'runtime',
    };
    if (task.with && typeof task.with.session === 'string') {
      policy.agentSessionRef = task.with.session;
    }
    policies.set(policyKey(policy), policy);
  }

  return policies.size > 0 ? [...policies.values()] : undefined;
}
