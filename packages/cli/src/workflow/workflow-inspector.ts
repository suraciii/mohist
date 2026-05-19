import { Stage } from '../types';
import {
  MOHIST_DEFAULT_WORKFLOW_DEFINITION,
  createWorkflowDefinitionSnapshot,
  type CheckDefinition,
  type CheckFailurePolicy,
  type StageDefinition,
  type TaskDefinition,
  type WorkflowDefinitionSnapshot,
} from './domain';

export type WorkflowDiagnosticSeverity = 'error' | 'warning';

export interface WorkflowDiagnostic {
  severity: WorkflowDiagnosticSeverity;
  path: string;
  message: string;
  suggestion?: string;
}

export interface ResolvedWorkflowDefinition {
  snapshot: WorkflowDefinitionSnapshot;
  sourceChain: string[];
  diagnostics: WorkflowDiagnostic[];
}

export type ExplainedWorkflowItem =
  | {
    kind: 'task';
    stage: Stage;
    id: string;
    title: string;
    source: string;
    uses: string;
    dependsOn: string[];
    resultContract?: string;
    selfRepair?: boolean;
  }
  | {
    kind: 'check';
    stage: Stage;
    id: string;
    title: string;
    source: string;
    uses: string;
    phase: string;
    blocking: boolean;
    reaction?: CheckFailurePolicy;
  };

export function resolveWorkflowDefinition(): ResolvedWorkflowDefinition {
  return {
    snapshot: createWorkflowDefinitionSnapshot({
      definition: MOHIST_DEFAULT_WORKFLOW_DEFINITION,
      source: { type: 'builtin', id: MOHIST_DEFAULT_WORKFLOW_DEFINITION.id },
    }),
    sourceChain: ['mohist/default'],
    diagnostics: [],
  };
}

export function validateWorkflowDefinition(resolved: ResolvedWorkflowDefinition = resolveWorkflowDefinition()): WorkflowDiagnostic[] {
  const diagnostics: WorkflowDiagnostic[] = [...resolved.diagnostics];
  const seenStages = new Set<Stage>();

  for (const [stageIndex, stage] of resolved.snapshot.compiledStageDefinitions.entries()) {
    const stagePath = `stages[${stageIndex}]`;
    if (seenStages.has(stage.stage)) {
      diagnostics.push({
        severity: 'error',
        path: `${stagePath}.stage`,
        message: `Duplicate stage '${stage.stage}'`,
        suggestion: 'Keep one definition for each workflow stage.',
      });
    }
    seenStages.add(stage.stage);

    const taskIds = new Set(stage.tasks.map(task => task.id));
    for (const [taskIndex, task] of stage.tasks.entries()) {
      if (!task.id.trim()) {
        diagnostics.push({
          severity: 'error',
          path: `${stagePath}.tasks[${taskIndex}].id`,
          message: 'Task id is required',
        });
      }
      for (const dependency of task.dependsOn ?? []) {
        if (!taskIds.has(dependency)) {
          diagnostics.push({
            severity: 'error',
            path: `${stagePath}.tasks[${taskIndex}].dependsOn`,
            message: `Task '${task.id}' depends on unknown task '${dependency}'`,
            suggestion: 'Use a task id declared in the same stage.',
          });
        }
      }
    }

    const checkNames = new Set(stage.checks.map(check => check.name));
    for (const policy of stage.checkPolicies ?? []) {
      if (!checkNames.has(policy.checkName)) {
        diagnostics.push({
          severity: 'error',
          path: `${stagePath}.checkPolicies`,
          message: `Check policy references unknown check '${policy.checkName}'`,
        });
      }
    }
    for (const repair of stage.repairPolicies ?? []) {
      if (!checkNames.has(repair.checkName)) {
        diagnostics.push({
          severity: 'error',
          path: `${stagePath}.repairPolicies`,
          message: `Repair policy references unknown check '${repair.checkName}'`,
        });
      }
    }
  }

  return diagnostics;
}

export function explainWorkflowItem(
  itemId: string,
  resolved: ResolvedWorkflowDefinition = resolveWorkflowDefinition(),
): ExplainedWorkflowItem | null {
  for (const stage of resolved.snapshot.compiledStageDefinitions) {
    const task = stage.tasks.find(candidate => candidate.id === itemId);
    if (task) return explainTask(stage, task);

    const check = stage.checks.find(candidate => candidate.name === itemId);
    if (check) return explainCheck(stage, check);
  }
  return null;
}

function explainTask(stage: StageDefinition, task: TaskDefinition): ExplainedWorkflowItem {
  const policy = stage.taskExecutionPolicies?.find(candidate => candidate.taskId === task.id)
    ?? stage.taskExecutionPolicies?.find(candidate => candidate.taskId === '*');
  return {
    kind: 'task',
    stage: stage.stage,
    id: task.id,
    title: task.title,
    source: 'builtin',
    uses: policy?.kind ?? 'agent-session',
    dependsOn: task.dependsOn ?? [],
    resultContract: task.resultContract?.kind,
    selfRepair: task.selfRepairPolicy?.enabled,
  };
}

function explainCheck(stage: StageDefinition, check: CheckDefinition): ExplainedWorkflowItem {
  const phase = stage.checkPolicies?.find(candidate => candidate.checkName === check.name)?.phase ?? 'post-task';
  const reaction = stage.repairPolicies?.find(candidate => candidate.checkName === check.name)
    ?? stage.checkFailurePolicies?.find(candidate => candidate.checkName === check.name);
  return {
    kind: 'check',
    stage: stage.stage,
    id: check.name,
    title: check.title,
    source: 'builtin',
    uses: inferCheckUses(check.name),
    phase,
    blocking: true,
    reaction,
  };
}

function inferCheckUses(checkName: string): string {
  if (checkName.startsWith('health:')) return 'mohist/health-gate';
  if (checkName === 'review-passed' || checkName === 'self-review-passed') return 'mohist/verdict';
  if (checkName === 'merge-ready') return 'mohist/merge-ready';
  return 'mohist/check';
}
