import type { WorkflowDeliveryRequirement } from '../../types';
import { getWorkflowUseDefinition, inferWorkflowCheckUse, inferWorkflowTaskUse, type WorkflowDeliveryRole } from '../uses-catalog';
import type { CheckDefinition, CompiledStageDefinition, TaskDefinition, TaskExecutionPolicy, WorkflowDefinitionSnapshot } from './workflow-definition';

export const DEFAULT_WORKFLOW_DELIVERY_REQUIREMENT: WorkflowDeliveryRequirement = {
  mode: 'local-merge',
  requiresLocalMerge: true,
  requiresRemoteMerge: false,
  falseDoneApplicable: true,
};

function deliveryRequirementForRole(role: WorkflowDeliveryRole | null): WorkflowDeliveryRequirement {
  if (role === 'local-merge') {
    return DEFAULT_WORKFLOW_DELIVERY_REQUIREMENT;
  }
  if (role === 'remote-merge') {
    return {
      mode: 'remote-merge',
      requiresLocalMerge: false,
      requiresRemoteMerge: true,
      falseDoneApplicable: false,
    };
  }
  if (role === 'remote-pr') {
    return {
      mode: 'handoff',
      requiresLocalMerge: false,
      requiresRemoteMerge: false,
      falseDoneApplicable: false,
    };
  }
  return {
    mode: 'none',
    requiresLocalMerge: false,
    requiresRemoteMerge: false,
    falseDoneApplicable: false,
  };
}

function deliveryRoleForUse(useName: string | undefined): WorkflowDeliveryRole {
  return getWorkflowUseDefinition(useName ?? '')?.deliveryRole ?? 'none';
}

function taskExecutionKind(stage: CompiledStageDefinition, task: TaskDefinition): string | undefined {
  const staticPolicy = stage.taskExecutionPolicies?.find(policy =>
    policy.taskId === task.id && policy.workSourceKind !== 'runtime',
  );
  const anyPolicy = stage.taskExecutionPolicies?.find(policy => policy.taskId === task.id);
  return (staticPolicy ?? anyPolicy)?.kind;
}

function taskDeliveryRole(stage: CompiledStageDefinition, task: TaskDefinition): WorkflowDeliveryRole {
  const executionKind = taskExecutionKind(stage, task);
  return deliveryRoleForUse(task.uses ?? inferWorkflowTaskUse(task.id, executionKind));
}

function tasksFromDeliveryRole(stage: CompiledStageDefinition): WorkflowDeliveryRole {
  return deliveryRoleForUse(stage.tasksFrom);
}

function checkDeliveryRole(check: CheckDefinition): WorkflowDeliveryRole {
  return deliveryRoleForUse(check.uses ?? inferWorkflowCheckUse(check.name));
}

function checkRetryTaskDeliveryRole(stage: CompiledStageDefinition, check: CheckDefinition): WorkflowDeliveryRole | null {
  const task = check.onFailure?.retry?.task;
  if (!task) return null;
  return taskDeliveryRole(stage, task);
}

function runtimePolicyDeliveryRole(policy: TaskExecutionPolicy): WorkflowDeliveryRole {
  return deliveryRoleForUse(inferWorkflowTaskUse(policy.taskId, policy.kind));
}

function strongestDeliveryRole(roles: WorkflowDeliveryRole[]): WorkflowDeliveryRole | null {
  if (roles.includes('local-merge')) return 'local-merge';
  if (roles.includes('remote-merge')) return 'remote-merge';
  if (roles.includes('remote-pr')) return 'remote-pr';
  return null;
}

export function projectWorkflowDeliveryRequirement(
  snapshot: WorkflowDefinitionSnapshot | null | undefined,
): WorkflowDeliveryRequirement {
  if (!snapshot) return DEFAULT_WORKFLOW_DELIVERY_REQUIREMENT;

  const roles: WorkflowDeliveryRole[] = [];
  for (const stage of snapshot.compiledStageDefinitions) {
    roles.push(tasksFromDeliveryRole(stage));
    roles.push(...stage.tasks.map(task => taskDeliveryRole(stage, task)));
    roles.push(...stage.checks.map(checkDeliveryRole));
    roles.push(...stage.checks
      .map(check => checkRetryTaskDeliveryRole(stage, check))
      .filter((role): role is WorkflowDeliveryRole => role !== null));
    roles.push(...(stage.taskExecutionPolicies ?? [])
      .filter(policy => policy.workSourceKind === 'runtime')
      .map(runtimePolicyDeliveryRole));
  }

  return deliveryRequirementForRole(strongestDeliveryRole(roles));
}

export function workflowRequiresLocalMerge(
  snapshot: WorkflowDefinitionSnapshot | null | undefined,
): boolean {
  return projectWorkflowDeliveryRequirement(snapshot).requiresLocalMerge;
}
