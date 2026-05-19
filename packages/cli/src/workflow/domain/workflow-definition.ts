import { Stage } from '../../types';
import { WorkflowDomainError } from './errors';
import type {
  CheckFailurePolicy,
  InvalidationPolicy,
  RepairPolicy,
  StageDefinition,
  TaskDefinition,
  WorkflowDefinition,
  WorkflowDefinitionSnapshot,
  WorkflowDefinitionSource,
} from './types';

function cloneTaskDefinition(task: TaskDefinition): TaskDefinition {
  return {
    ...task,
    dependsOn: task.dependsOn ? [...task.dependsOn] : undefined,
  };
}

function cloneCheckFailurePolicy(policy: CheckFailurePolicy): CheckFailurePolicy {
  return {
    ...policy,
    inputFrom: policy.inputFrom?.map(input => ({ ...input })),
  };
}

function cloneRepairPolicy(policy: RepairPolicy): RepairPolicy {
  return {
    ...policy,
    inputFrom: policy.inputFrom?.map(input => ({ ...input })),
  };
}

function cloneInvalidationPolicy(policy: InvalidationPolicy): InvalidationPolicy {
  return {
    entries: policy.entries.map(entry => ({
      ...entry,
      when: entry.when ? { ...entry.when } : undefined,
      invalidates: {
        tasks: entry.invalidates.tasks ? [...entry.invalidates.tasks] : undefined,
        checks: entry.invalidates.checks ? [...entry.invalidates.checks] : undefined,
        approval: entry.invalidates.approval,
      },
    })),
  };
}

function cloneStageDefinition(stage: StageDefinition): StageDefinition {
  return {
    ...stage,
    tasks: stage.tasks.map(cloneTaskDefinition),
    checks: stage.checks.map(check => ({
      ...check,
      with: check.with ? { ...check.with } : undefined,
    })),
    checkFailurePolicies: stage.checkFailurePolicies?.map(cloneCheckFailurePolicy),
    workSources: stage.workSources?.map(source => ({
      ...source,
      taskIds: source.taskIds ? [...source.taskIds] : undefined,
    })),
    taskExecutionPolicies: stage.taskExecutionPolicies?.map(policy => ({ ...policy })),
    checkPolicies: stage.checkPolicies?.map(policy => ({ ...policy })),
    approvalPolicy: stage.approvalPolicy ? { ...stage.approvalPolicy } : undefined,
    repairPolicies: stage.repairPolicies?.map(cloneRepairPolicy),
    invalidationPolicy: stage.invalidationPolicy ? cloneInvalidationPolicy(stage.invalidationPolicy) : undefined,
  };
}

export function compileWorkflowDefinition(definition: WorkflowDefinition): StageDefinition[] {
  if (!definition.id || definition.id.trim().length === 0) {
    throw new WorkflowDomainError('WorkflowDefinition requires an id');
  }
  if (!Array.isArray(definition.stages) || definition.stages.length === 0) {
    throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} requires at least one stage`);
  }

  const seenStages = new Set<Stage>();
  for (const stage of definition.stages) {
    if (seenStages.has(stage.stage)) {
      throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} declares duplicate stage ${stage.stage}`);
    }
    seenStages.add(stage.stage);

    const taskIds = new Set<string>();
    for (const task of stage.tasks) {
      if (taskIds.has(task.id)) {
        throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} declares duplicate task ${stage.stage}:${task.id}`);
      }
      taskIds.add(task.id);
    }

    const checkNames = new Set<string>();
    for (const check of stage.checks) {
      if (checkNames.has(check.name)) {
        throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} declares duplicate check ${stage.stage}:${check.name}`);
      }
      checkNames.add(check.name);
    }

    for (const policy of stage.checkPolicies ?? []) {
      if (!checkNames.has(policy.checkName)) {
        throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} check policy references unknown check ${stage.stage}:${policy.checkName}`);
      }
    }

    if (stage.approvalPolicy && stage.approvalCheckName && stage.approvalPolicy.checkName !== stage.approvalCheckName) {
      throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} has inconsistent approval check for ${stage.stage}`);
    }
  }

  return definition.stages.map(cloneStageDefinition);
}

export function cloneWorkflowDefinition(definition: WorkflowDefinition): WorkflowDefinition {
  return {
    ...definition,
    stages: definition.stages.map(cloneStageDefinition),
    defaults: definition.defaults ? { ...definition.defaults } : undefined,
  };
}

export function createWorkflowDefinitionSnapshot(input: {
  definition: WorkflowDefinition;
  source?: WorkflowDefinitionSource;
  capturedAt?: string;
}): WorkflowDefinitionSnapshot {
  const resolvedDefinition = cloneWorkflowDefinition(input.definition);
  const compiledStageDefinitions = compileWorkflowDefinition(resolvedDefinition);
  return {
    workflowId: resolvedDefinition.id,
    name: resolvedDefinition.name,
    source: input.source ?? { type: 'runtime', id: resolvedDefinition.id },
    resolvedDefinition,
    compiledStageDefinitions,
    capturedAt: input.capturedAt ?? new Date().toISOString(),
  };
}

export function cloneWorkflowDefinitionSnapshot(snapshot: WorkflowDefinitionSnapshot): WorkflowDefinitionSnapshot {
  return {
    workflowId: snapshot.workflowId,
    name: snapshot.name,
    source: { ...snapshot.source },
    resolvedDefinition: cloneWorkflowDefinition(snapshot.resolvedDefinition),
    compiledStageDefinitions: snapshot.compiledStageDefinitions.map(cloneStageDefinition),
    capturedAt: snapshot.capturedAt,
  };
}
