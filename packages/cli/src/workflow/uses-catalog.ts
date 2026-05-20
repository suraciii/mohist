export type WorkflowUsePlacement = 'task' | 'check' | 'both';
export type WorkflowUseSideEffect = 'none' | 'worktree' | 'spec-state' | 'archive' | 'branch' | 'remote-pr' | 'merge';
export type WorkflowUseIdempotency = 'idempotent' | 'checkpointed' | 'irreversible' | 'unknown';
export type WorkflowDeliveryRole = 'none' | 'spec-sync' | 'archive' | 'local-merge' | 'remote-pr' | 'remote-merge';
export type WorkflowTaskSourceKind = 'ralph';

export interface WorkflowUseEvidenceRequirement {
  requiredFields?: string[];
  anyOfFields?: string[];
}

export interface WorkflowUseDefinition {
  name: string;
  allowedPlacement: WorkflowUsePlacement;
  mutates: boolean;
  sideEffect: WorkflowUseSideEffect;
  idempotency: WorkflowUseIdempotency;
  deliveryRole: WorkflowDeliveryRole;
  locksCode?: boolean;
  sourceKind?: WorkflowTaskSourceKind;
  raises?: string[];
  evidence?: WorkflowUseEvidenceRequirement;
  description: string;
  inputs: string[];
  outputContract: string;
}

export const BUILTIN_WORKFLOW_USES: WorkflowUseDefinition[] = [
  {
    name: 'mohist/agent',
    allowedPlacement: 'task',
    mutates: true,
    sideEffect: 'worktree',
    idempotency: 'checkpointed',
    deliveryRole: 'none',
    raises: ['code.changed'],
    description: 'Runs an agent task through Mohist ACP session execution, creating or reusing the task session as needed.',
    inputs: ['prompt', 'context', 'outputContract', 'session'],
    outputContract: 'ACP-backed agent task result, session evidence, and declared artifacts.',
  },
  {
    name: 'mohist/shell',
    allowedPlacement: 'both',
    mutates: false,
    sideEffect: 'none',
    idempotency: 'unknown',
    deliveryRole: 'none',
    description: 'Runs a local shell command. As a check it is read-only by contract; as a task it may mutate only when explicitly used as task work.',
    inputs: ['command', 'timeout', 'cwd'],
    outputContract: 'Exit code, stdout/stderr excerpt, and command metadata.',
  },
  {
    name: 'mohist/artifact-exists',
    allowedPlacement: 'check',
    mutates: false,
    sideEffect: 'none',
    idempotency: 'idempotent',
    deliveryRole: 'none',
    description: 'Checks that a declared artifact exists.',
    inputs: ['path'],
    outputContract: 'PASS when the artifact exists, FAIL otherwise.',
  },
  {
    name: 'mohist/verdict',
    allowedPlacement: 'check',
    mutates: false,
    sideEffect: 'none',
    idempotency: 'idempotent',
    deliveryRole: 'none',
    description: 'Reads a declared output source and verifies a structured PASS/FAIL verdict.',
    inputs: ['outputSource', 'allowedMarkers'],
    outputContract: 'Structured verdict evidence.',
  },
  {
    name: 'mohist/marker',
    allowedPlacement: 'check',
    mutates: false,
    sideEffect: 'none',
    idempotency: 'idempotent',
    deliveryRole: 'none',
    description: 'Reads a file path and verifies that it contains an expected marker.',
    inputs: ['path', 'expect', 'markers'],
    outputContract: 'PASS/FAIL marker evidence with the matched marker and path.',
  },
  {
    name: 'mohist/health-gate',
    allowedPlacement: 'check',
    mutates: false,
    sideEffect: 'none',
    idempotency: 'idempotent',
    deliveryRole: 'none',
    description: 'Runs the configured health command as read-only verification evidence for a stage.',
    inputs: ['command', 'timeout', 'autoFix', 'maxFixAttempts'],
    outputContract: 'PASS/FAIL health evidence with command metadata.',
  },
  {
    name: 'mohist/merge-ready',
    allowedPlacement: 'check',
    mutates: false,
    sideEffect: 'none',
    idempotency: 'idempotent',
    deliveryRole: 'none',
    description: 'Checks whether the issue branch can be merged into the target branch.',
    inputs: ['targetBranch', 'strategy'],
    outputContract: 'Mergeability snapshot and conflict metadata.',
  },
  {
    name: 'mohist/ralph-tasks',
    allowedPlacement: 'task',
    mutates: true,
    sideEffect: 'worktree',
    idempotency: 'checkpointed',
    deliveryRole: 'none',
    sourceKind: 'ralph',
    description: 'Executes generated OpenSpec tasks through Mohist task runtime.',
    inputs: ['tasksPath'],
    outputContract: 'Task completion evidence and artifacts.',
  },
  {
    name: 'mohist/openspec-sync',
    allowedPlacement: 'task',
    mutates: true,
    sideEffect: 'spec-state',
    idempotency: 'checkpointed',
    deliveryRole: 'spec-sync',
    description: 'Synchronizes OpenSpec change content into project specs.',
    inputs: ['changePath'],
    outputContract: 'Spec sync result.',
  },
  {
    name: 'mohist/archive-change',
    allowedPlacement: 'task',
    mutates: true,
    sideEffect: 'archive',
    idempotency: 'checkpointed',
    deliveryRole: 'archive',
    evidence: { anyOfFields: ['archivePath', 'success'] },
    description: 'Archives an OpenSpec change after delivery.',
    inputs: ['changePath'],
    outputContract: 'Archive path or success metadata.',
  },
  {
    name: 'mohist/merge',
    allowedPlacement: 'task',
    mutates: true,
    sideEffect: 'merge',
    idempotency: 'irreversible',
    deliveryRole: 'local-merge',
    locksCode: true,
    evidence: { requiredFields: ['landedSha'] },
    description: 'Merges the issue worktree branch into the base branch.',
    inputs: ['strategy', 'targetBranch'],
    outputContract: 'Delivery metadata including landed commit.',
  },
  {
    name: 'mohist/rebase',
    allowedPlacement: 'task',
    mutates: true,
    sideEffect: 'branch',
    idempotency: 'checkpointed',
    deliveryRole: 'none',
    raises: ['code.changed'],
    description: 'Rebases the issue branch onto the latest base branch.',
    inputs: ['targetBranch'],
    outputContract: 'Rebase result and changed snapshot metadata.',
  },
  {
    name: 'mohist/github-pr',
    allowedPlacement: 'task',
    mutates: true,
    sideEffect: 'remote-pr',
    idempotency: 'idempotent',
    deliveryRole: 'remote-pr',
    evidence: { requiredFields: ['prUrl'] },
    description: 'Creates or reuses a GitHub pull request for the issue branch.',
    inputs: ['base', 'title', 'body'],
    outputContract: 'Pull request URL, branch, base, and head metadata.',
  },
  {
    name: 'mohist/pr-ready',
    allowedPlacement: 'check',
    mutates: false,
    sideEffect: 'none',
    idempotency: 'idempotent',
    deliveryRole: 'none',
    description: 'Checks whether a pull request exists and is ready for handoff.',
    inputs: ['prUrl', 'branch'],
    outputContract: 'PASS when the pull request is open and mergeable enough for handoff.',
  },
  {
    name: 'mohist/pr-merged',
    allowedPlacement: 'check',
    mutates: false,
    sideEffect: 'none',
    idempotency: 'idempotent',
    deliveryRole: 'remote-merge',
    locksCode: true,
    evidence: { requiredFields: ['mergedSha'] },
    description: 'Checks whether a pull request has been merged remotely.',
    inputs: ['prUrl'],
    outputContract: 'Merged pull request metadata including the landed commit.',
  },
  {
    name: 'mohist/approval',
    allowedPlacement: 'check',
    mutates: false,
    sideEffect: 'none',
    idempotency: 'idempotent',
    deliveryRole: 'none',
    description: 'Waits for explicit user approval using the stage evidence.',
    inputs: ['evidence'],
    outputContract: 'Approval status and response metadata.',
  },
];

export function getWorkflowUseDefinition(name: string): WorkflowUseDefinition | undefined {
  return BUILTIN_WORKFLOW_USES.find(use => use.name === name);
}

export function workflowUsesThatRaise(eventName: string): string[] {
  return BUILTIN_WORKFLOW_USES
    .filter(use => use.allowedPlacement !== 'check' && use.raises?.includes(eventName))
    .map(use => use.name);
}

export function isWorkflowUseAllowed(name: string, placement: 'task' | 'check'): boolean {
  const use = getWorkflowUseDefinition(name);
  if (!use) return false;
  return use.allowedPlacement === 'both' || use.allowedPlacement === placement;
}

export function inferWorkflowCheckUse(checkName: string): string {
  if (checkName.startsWith('health:')) return 'mohist/health-gate';
  if (checkName === 'review-passed' || checkName === 'self-review-passed') return 'mohist/marker';
  if (checkName === 'merge-ready') return 'mohist/merge-ready';
  if (checkName.endsWith('-approval')) return 'mohist/approval';
  return 'mohist/artifact-exists';
}

export function inferWorkflowTaskUse(taskId: string, executionKind?: string): string {
  if (taskId === 'integrate:spec-sync') return 'mohist/openspec-sync';
  if (taskId === 'integrate:archive-change') return 'mohist/archive-change';
  if (taskId === 'integrate:merge') return 'mohist/merge';
  if (taskId === 'rebase-branch') return 'mohist/rebase';
  if (executionKind === 'ralph-task') return 'mohist/ralph-tasks';
  return 'mohist/agent';
}

export function unwrapWorkflowUseOutput(output: unknown): Record<string, unknown> | null {
  if (!output || typeof output !== 'object') return null;
  const data = output as Record<string, unknown>;
  if (data.kind === 'service-call-task' && data.result && typeof data.result === 'object') {
    return data.result as Record<string, unknown>;
  }
  return data;
}

function hasMeaningfulEvidenceValue(field: string, value: unknown): boolean {
  if (field === 'success') return value === true;
  if (typeof value === 'string') return value.length > 0;
  return value !== null && value !== undefined;
}

export function validateWorkflowUseEvidence(
  useName: string | undefined,
  output: unknown,
): { ok: true } | { ok: false; reason: 'unknown-use' | 'evidence-missing'; field?: string } {
  if (!useName) return { ok: true };
  const use = getWorkflowUseDefinition(useName);
  if (!use) return { ok: false, reason: 'unknown-use' };
  if (use.deliveryRole === 'none' && !use.locksCode && !use.evidence) return { ok: true };
  const evidence = use.evidence;
  if (!evidence) return { ok: true };
  const data = unwrapWorkflowUseOutput(output);
  if (!data) {
    return {
      ok: false,
      reason: 'evidence-missing',
      field: evidence.requiredFields?.[0] ?? evidence.anyOfFields?.join('|'),
    };
  }
  for (const field of evidence.requiredFields ?? []) {
    if (!hasMeaningfulEvidenceValue(field, data[field])) {
      return { ok: false, reason: 'evidence-missing', field };
    }
  }
  if (evidence.anyOfFields?.length && !evidence.anyOfFields.some(field => hasMeaningfulEvidenceValue(field, data[field]))) {
    return { ok: false, reason: 'evidence-missing', field: evidence.anyOfFields.join('|') };
  }
  return { ok: true };
}
