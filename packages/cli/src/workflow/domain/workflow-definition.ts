import { Stage } from '../../types';
import { WorkflowDomainError } from './errors';
import { inferWorkflowTaskUse } from '../uses-catalog';
import type {
  AgentPromptSource,
  CheckDefinition,
  CheckFailurePolicy,
  InvalidationPolicy,
  RepairPolicy,
  StageDefinition,
  TaskDefinition,
  WorkflowDefinition,
  WorkflowDefinitionSnapshot,
  WorkflowDefinitionSource,
  WorkSourceKind,
  TaskExecutionKind,
  TaskExecutionPolicy,
} from './types';

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function clonePromptSource(value: unknown): AgentPromptSource | string | undefined {
  if (typeof value === 'string') return value;
  if (!isRecord(value)) return undefined;
  if (typeof value.ref === 'string') return { ref: value.ref };
  if (typeof value.file === 'string') return { file: value.file };
  if (typeof value.inline === 'string') return { inline: value.inline };
  return undefined;
}

function normalizeAgentPromptSource(task: TaskDefinition): TaskDefinition {
  if (task.uses !== 'mohist/agent') return task;
  const withConfig = task.with ? { ...task.with } : {};
  const prompt = clonePromptSource(withConfig.prompt);
  if (prompt !== undefined) withConfig.prompt = prompt;
  if (typeof withConfig.promptFile === 'string' && prompt === undefined) {
    withConfig.prompt = { file: withConfig.promptFile };
    delete withConfig.promptFile;
  }
  return { ...task, with: Object.keys(withConfig).length > 0 ? withConfig : undefined };
}

function cloneTaskDefinition(task: TaskDefinition): TaskDefinition {
  const normalized = normalizeAgentPromptSource(task);
  return {
    ...normalized,
    with: normalized.with ? { ...normalized.with } : undefined,
    emits: normalized.emits ? [...normalized.emits] : undefined,
    dependsOn: normalized.dependsOn ? [...normalized.dependsOn] : undefined,
  };
}

function cloneOnFailure(check: CheckDefinition): CheckDefinition {
  if (!check.onFailure?.retry) return check;
  return {
    ...check,
    onFailure: {
      retry: {
        limit: check.onFailure.retry.limit,
        task: cloneTaskDefinition(check.onFailure.retry.task),
        inputFrom: check.onFailure.retry.inputFrom?.map(input => ({ ...input })),
      },
    },
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
      ...cloneOnFailure(check),
      with: check.with ? { ...check.with } : undefined,
    })),
    checkFailurePolicies: stage.checkFailurePolicies?.map(cloneCheckFailurePolicy),
    workSources: stage.workSources?.map(source => ({
      ...source,
      taskIds: source.taskIds ? [...source.taskIds] : undefined,
    })),
    on: stage.on ? Object.fromEntries(Object.entries(stage.on).map(([event, policy]) => [event, { ...policy }])) : undefined,
    taskExecutionPolicies: stage.taskExecutionPolicies?.map(policy => ({ ...policy })),
    checkPolicies: stage.checkPolicies?.map(policy => ({ ...policy })),
    approvalPolicy: stage.approvalPolicy ? { ...stage.approvalPolicy } : undefined,
    repairPolicies: stage.repairPolicies?.map(cloneRepairPolicy),
    invalidationPolicy: stage.invalidationPolicy ? cloneInvalidationPolicy(stage.invalidationPolicy) : undefined,
  };
}

function compileRepairPoliciesFromChecks(stage: StageDefinition): RepairPolicy[] {
  return stage.checks.flatMap(check => {
    const retry = check.onFailure?.retry;
    if (!retry) return [];
    return [{
      checkName: check.name,
      fixTaskId: retry.task.id,
      fixTaskTitle: retry.task.title,
      maxAttempts: retry.limit,
      inputFrom: retry.inputFrom?.map(input => ({ ...input })),
    }];
  });
}

function inferTaskExecutionKind(taskId: string, uses?: string): TaskExecutionKind {
  const resolvedUses = uses ?? inferWorkflowTaskUse(taskId);
  if (resolvedUses === 'mohist/ralph-tasks') return 'ralph-task';
  if (
    resolvedUses === 'mohist/openspec-sync'
    || resolvedUses === 'mohist/archive-change'
    || resolvedUses === 'mohist/merge'
    || resolvedUses === 'mohist/github-pr'
  ) {
    return 'service-call';
  }
  if (resolvedUses === 'mohist/rebase') return 'rebase-task';
  return 'agent-session';
}

function promptSourceKind(task: TaskDefinition): 'inline' | 'file' | 'ref' | null {
  const prompt = task.with?.prompt;
  if (typeof prompt === 'string') return 'inline';
  if (!prompt || typeof prompt !== 'object' || Array.isArray(prompt)) return null;
  const data = prompt as Record<string, unknown>;
  if (typeof data.inline === 'string') return 'inline';
  if (typeof data.file === 'string') return 'file';
  if (typeof data.ref === 'string') return 'ref';
  return null;
}

function inferRepairTaskExecutionKind(task: TaskDefinition): TaskExecutionKind {
  if (task.uses === 'mohist/rebase') return 'rebase-task';
  if (task.uses === 'mohist/agent') {
    const promptKind = promptSourceKind(task);
    if (promptKind === 'inline' || promptKind === 'file') return 'agent-session';
  }
  return 'repair-task';
}

function taskWorkSourceKind(stage: StageDefinition, taskId: string): WorkSourceKind | undefined {
  for (const source of stage.workSources ?? []) {
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

function compileTaskExecutionPolicies(stage: StageDefinition): TaskExecutionPolicy[] | undefined {
  const policies = new Map<string, TaskExecutionPolicy>();

  for (const policy of stage.taskExecutionPolicies ?? []) {
    policies.set(policyKey(policy), { ...policy });
  }

  for (const task of stage.tasks) {
    const workSourceKind = taskWorkSourceKind(stage, task.id);
    const existing = stage.taskExecutionPolicies?.find(policy => policy.taskId === task.id && policy.workSourceKind === workSourceKind);
    if (existing) continue;

    const kind = inferTaskExecutionKind(task.id, task.uses);
    const policy: TaskExecutionPolicy = {
      taskId: task.id,
      kind,
      workSourceKind,
    };

    if (kind === 'agent-session' && task.with && typeof task.with.session === 'string') {
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
      kind: inferRepairTaskExecutionKind(task),
      workSourceKind: 'runtime',
    };
    policies.set(policyKey(policy), policy);
  }

  return policies.size > 0 ? [...policies.values()] : undefined;
}

function allCheckNames(stage: StageDefinition): string[] {
  return stage.checkPolicies?.filter(policy => policy.phase !== 'approval').map(policy => policy.checkName)
    ?? stage.checks.map(check => check.name);
}

function compileInvalidationPolicyFromStageEvents(stage: StageDefinition): InvalidationPolicy | undefined {
  const eventPolicies = stage.on ? Object.entries(stage.on) : [];
  if (eventPolicies.length === 0) return undefined;

  const entries: InvalidationPolicy['entries'] = [];
  const taskDefinitions = [
    ...stage.tasks,
    ...stage.checks.flatMap(check => check.onFailure?.retry?.task ? [check.onFailure.retry.task] : []),
  ];

  for (const task of taskDefinitions) {
    for (const eventName of task.emits ?? []) {
      const eventPolicy = stage.on?.[eventName];
      if (!eventPolicy) continue;
      const invalidates: InvalidationPolicy['entries'][number]['invalidates'] = {};
      if (eventPolicy.reset === 'checks' || eventPolicy.reset === 'checks-and-approval') {
        if (stage.stage === Stage.Check && eventName === 'code.changed') {
          invalidates.tasks = ['ai-review'];
        }
        invalidates.checks = allCheckNames(stage);
      }
      if (eventPolicy.reset === 'approval' || eventPolicy.reset === 'checks-and-approval') {
        invalidates.approval = true;
      }
      entries.push({
        trigger: 'task-completion',
        triggerTaskId: task.id,
        reason: `${eventName} reset ${eventPolicy.reset}`,
        invalidates,
      });
    }
  }

  return entries.length > 0 ? { entries } : undefined;
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

    for (const repair of stage.repairPolicies ?? []) {
      if (!checkNames.has(repair.checkName)) {
        throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} repair policy references unknown check ${stage.stage}:${repair.checkName}`);
      }
    }

    if (stage.approvalPolicy && stage.approvalCheckName && stage.approvalPolicy.checkName !== stage.approvalCheckName) {
      throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} has inconsistent approval check for ${stage.stage}`);
    }
  }

  return definition.stages.map(stage => {
    const compiled = cloneStageDefinition(stage);
    const repairPoliciesFromChecks = compileRepairPoliciesFromChecks(compiled);
    if (repairPoliciesFromChecks.length > 0) {
      const onFailureCheckNames = new Set(repairPoliciesFromChecks.map(policy => policy.checkName));
      compiled.repairPolicies = [
        ...(compiled.repairPolicies ?? []).filter(policy => !onFailureCheckNames.has(policy.checkName)),
        ...repairPoliciesFromChecks,
      ];
      compiled.checkFailurePolicies = [
        ...(compiled.checkFailurePolicies ?? []).filter(policy => !onFailureCheckNames.has(policy.checkName)),
        ...repairPoliciesFromChecks.map(policy => ({ ...policy })),
      ];
    }
    const eventInvalidationPolicy = compileInvalidationPolicyFromStageEvents(compiled);
    if (eventInvalidationPolicy) {
      compiled.invalidationPolicy = {
        entries: [
          ...(compiled.invalidationPolicy?.entries ?? []),
          ...eventInvalidationPolicy.entries,
        ],
      };
    }
    compiled.taskExecutionPolicies = compileTaskExecutionPolicies(compiled);
    return compiled;
  });
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
