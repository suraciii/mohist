import { Stage } from '../../types';
import { WorkflowDomainError } from './errors';
import { getWorkflowUseDefinition, inferWorkflowTaskUse } from '../uses-catalog';
import type {
  AgentPromptSource,
  CheckDefinition,
  CheckFailurePolicy,
  ApprovalPolicy,
  ApprovalEvidencePolicy,
  CheckPolicy,
  CompiledStageDefinition,
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
  WorkSourceDefinition,
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
    checks: stage.checks.map(cloneOnFailure),
    on: stage.on ? Object.fromEntries(Object.entries(stage.on).map(([event, policy]) => [event, {
      ...policy,
      tasks: policy.tasks ? [...policy.tasks] : undefined,
      checks: Array.isArray(policy.checks) ? [...policy.checks] : policy.checks,
    }])) : undefined,
  };
}

function cloneCompiledStageDefinition(stage: CompiledStageDefinition): CompiledStageDefinition {
  return {
    ...cloneStageDefinition(stage),
    checkFailurePolicies: stage.checkFailurePolicies?.map(cloneCheckFailurePolicy),
    workSources: stage.workSources?.map(source => ({
      ...source,
      taskIds: source.taskIds ? [...source.taskIds] : undefined,
    })),
    taskExecutionPolicies: stage.taskExecutionPolicies?.map(policy => ({ ...policy })),
    checkPolicies: stage.checkPolicies.map(policy => ({ ...policy })),
    approvalPolicy: stage.approvalPolicy ? { ...stage.approvalPolicy } : undefined,
    approvalEvidencePolicy: stage.approvalEvidencePolicy ? { ...stage.approvalEvidencePolicy } : undefined,
    repairPolicies: stage.repairPolicies?.map(cloneRepairPolicy),
    invalidationPolicy: stage.invalidationPolicy ? cloneInvalidationPolicy(stage.invalidationPolicy) : undefined,
  };
}

function compileWorkSources(stage: StageDefinition, existingSources?: WorkSourceDefinition[]): WorkSourceDefinition[] | undefined {
  const workSources: WorkSourceDefinition[] = [];
  if (stage.tasks.length > 0) {
    workSources.push({ kind: 'static', taskIds: stage.tasks.map(task => task.id) });
  }
  if (stage.tasksFrom) {
    const sourceKind = getWorkflowUseDefinition(stage.tasksFrom)?.sourceKind;
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

function compileCheckPolicies(stage: StageDefinition, existingPolicies?: CheckPolicy[]): CheckPolicy[] {
  if (existingPolicies) return existingPolicies.map(policy => ({ ...policy }));
  return stage.checks.map(check => ({ checkName: check.name, phase: 'post-task' as const }));
}

function compileApprovalPolicy(stage: StageDefinition, existingPolicy?: ApprovalPolicy): ApprovalPolicy | undefined {
  if (existingPolicy) return { ...existingPolicy };
  if (stage.requiresApproval) return { checkName: stage.approvalCheckName ?? 'user-approval' };
  return undefined;
}

function approvalEvidenceRole(check: CheckDefinition): string | null {
  const evidence = check.with?.approvalEvidence;
  if (!evidence || typeof evidence !== 'object' || Array.isArray(evidence)) return null;
  const role = (evidence as Record<string, unknown>).role;
  return typeof role === 'string' ? role : null;
}

function compileApprovalEvidencePolicy(stage: StageDefinition): ApprovalEvidencePolicy | undefined {
  const verdictCheckName = stage.checks.find(check => approvalEvidenceRole(check) === 'verdict')?.name;
  const verificationCheckName = stage.checks.find(check => approvalEvidenceRole(check) === 'verification')?.name;
  const candidateCheckName = stage.checks.find(check => approvalEvidenceRole(check) === 'candidate')?.name;
  if (!verdictCheckName || !verificationCheckName || !candidateCheckName) return undefined;
  return {
    verdictCheckName,
    verificationCheckName,
    candidateCheckName,
    convergenceTaskId: 'check:converge-review-snapshot',
    convergenceTaskTitle: 'Converge review snapshot',
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

function taskWorkSourceKind(stage: StageDefinition, taskId: string, workSources: WorkSourceDefinition[] | undefined): WorkSourceKind | undefined {
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

function compileTaskExecutionPolicies(stage: StageDefinition, compiled: Partial<CompiledStageDefinition>): TaskExecutionPolicy[] | undefined {
  const policies = new Map<string, TaskExecutionPolicy>();

  for (const policy of compiled.taskExecutionPolicies ?? []) {
    policies.set(policyKey(policy), { ...policy });
  }

  for (const task of stage.tasks) {
    const workSourceKind = taskWorkSourceKind(stage, task.id, compiled.workSources);
    const existing = compiled.taskExecutionPolicies?.find(policy => policy.taskId === task.id && policy.workSourceKind === workSourceKind);
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

  for (const policy of compileRuntimeTaskExecutionPolicies(stage)) {
    if (!policies.has(policyKey(policy))) {
      policies.set(policyKey(policy), policy);
    }
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

function compileRuntimeTaskExecutionPolicies(stage: StageDefinition): TaskExecutionPolicy[] {
  const policies: TaskExecutionPolicy[] = [
    { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
  ];

  if (compileApprovalEvidencePolicy(stage)?.convergenceTaskId) {
    policies.push({ taskId: 'check:converge-review-snapshot', kind: 'service-call', workSourceKind: 'runtime' });
  }

  return policies;
}

function allCheckNames(stage: StageDefinition, checkPolicies?: CheckPolicy[]): string[] {
  return checkPolicies?.filter(policy => policy.phase !== 'approval').map(policy => policy.checkName)
    ?? stage.checks.map(check => check.name);
}

function checkNamesForEventPolicy(stage: StageDefinition, eventPolicy: NonNullable<StageDefinition['on']>[string], checkPolicies?: CheckPolicy[]): string[] | undefined {
  if (Array.isArray(eventPolicy.checks)) return [...eventPolicy.checks];
  if (eventPolicy.checks === 'all') return allCheckNames(stage, checkPolicies);
  if (eventPolicy.reset === 'checks' || eventPolicy.reset === 'checks-and-approval') return allCheckNames(stage, checkPolicies);
  return undefined;
}

function compileInvalidationPolicyFromStageEvents(stage: StageDefinition, checkPolicies?: CheckPolicy[]): InvalidationPolicy | undefined {
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
      if (eventPolicy.tasks?.length) {
        invalidates.tasks = [...eventPolicy.tasks];
      }
      const checks = checkNamesForEventPolicy(stage, eventPolicy, checkPolicies);
      if (checks?.length) {
        invalidates.checks = checks;
      }
      if (eventPolicy.approval || eventPolicy.reset === 'approval' || eventPolicy.reset === 'checks-and-approval') {
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

function compileRuntimeInvalidationPolicy(stage: StageDefinition, checkPolicies?: CheckPolicy[]): InvalidationPolicy | undefined {
  if (!stage.on?.['code.changed']) return undefined;
  const codeChangedPolicy = stage.on['code.changed'];
  const checks = checkNamesForEventPolicy(stage, codeChangedPolicy, checkPolicies);
  return {
    entries: [
      {
        trigger: 'task-completion',
        triggerTaskId: 'rebase-branch',
        when: { shaChanged: true },
        reason: 'code.changed reset checks-and-approval',
        invalidates: {
          tasks: codeChangedPolicy.tasks ? [...codeChangedPolicy.tasks] : undefined,
          checks,
          approval: codeChangedPolicy.approval || codeChangedPolicy.reset === 'approval' || codeChangedPolicy.reset === 'checks-and-approval',
        },
      },
    ],
  };
}

export function compileWorkflowDefinition(definition: WorkflowDefinition): CompiledStageDefinition[] {
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

    if (stage.approvalCheckName && stage.approvalCheckName !== 'user-approval' && !checkNames.has(stage.approvalCheckName)) {
      throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} approval references unknown check ${stage.stage}:${stage.approvalCheckName}`);
    }
  }

  return definition.stages.map(stage => {
    const source = cloneStageDefinition(stage);
    const compiled: CompiledStageDefinition = {
      ...source,
      workSources: compileWorkSources(source),
      checkPolicies: compileCheckPolicies(source),
      approvalPolicy: compileApprovalPolicy(source),
      approvalEvidencePolicy: compileApprovalEvidencePolicy(source),
    };
    const repairPoliciesFromChecks = compileRepairPoliciesFromChecks(source);
    if (repairPoliciesFromChecks.length > 0) {
      compiled.repairPolicies = repairPoliciesFromChecks;
      compiled.checkFailurePolicies = repairPoliciesFromChecks.map(policy => ({ ...policy }));
    }
    const eventInvalidationPolicy = compileInvalidationPolicyFromStageEvents(source, compiled.checkPolicies);
    if (eventInvalidationPolicy) {
      compiled.invalidationPolicy = {
        entries: [
          ...(compiled.invalidationPolicy?.entries ?? []),
          ...eventInvalidationPolicy.entries,
        ],
      };
    }
    const runtimeInvalidationPolicy = compileRuntimeInvalidationPolicy(source, compiled.checkPolicies);
    if (runtimeInvalidationPolicy) {
      compiled.invalidationPolicy = {
        entries: [
          ...(compiled.invalidationPolicy?.entries ?? []),
          ...runtimeInvalidationPolicy.entries,
        ],
      };
    }
    compiled.taskExecutionPolicies = compileTaskExecutionPolicies(source, compiled);
    return compiled;
  });
}

export function cloneWorkflowDefinition(definition: WorkflowDefinition): WorkflowDefinition {
  return {
    ...definition,
    stages: definition.stages.map(cloneStageDefinition),
    defaults: definition.defaults ? { ...definition.defaults } : undefined,
    artifacts: definition.artifacts ? { ...definition.artifacts } : undefined,
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
    compiledStageDefinitions: snapshot.compiledStageDefinitions.map(cloneCompiledStageDefinition),
    capturedAt: snapshot.capturedAt,
  };
}
