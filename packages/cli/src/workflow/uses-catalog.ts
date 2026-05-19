export type WorkflowUsePlacement = 'task' | 'check' | 'both';

export interface WorkflowUseDefinition {
  name: string;
  allowedPlacement: WorkflowUsePlacement;
  mutates: boolean;
  description: string;
  inputs: string[];
  outputContract: string;
}

export const BUILTIN_WORKFLOW_USES: WorkflowUseDefinition[] = [
  {
    name: 'mohist/agent',
    allowedPlacement: 'task',
    mutates: true,
    description: 'Runs an agent task through Mohist ACP session execution, creating or reusing the task session as needed.',
    inputs: ['prompt', 'context', 'outputContract', 'session'],
    outputContract: 'ACP-backed agent task result, session evidence, and declared artifacts.',
  },
  {
    name: 'mohist/shell',
    allowedPlacement: 'both',
    mutates: false,
    description: 'Runs a local shell command. As a check it is read-only by contract; as a task it may mutate only when explicitly used as task work.',
    inputs: ['command', 'timeout', 'cwd'],
    outputContract: 'Exit code, stdout/stderr excerpt, and command metadata.',
  },
  {
    name: 'mohist/artifact-exists',
    allowedPlacement: 'check',
    mutates: false,
    description: 'Checks that a declared artifact exists.',
    inputs: ['path'],
    outputContract: 'PASS when the artifact exists, FAIL otherwise.',
  },
  {
    name: 'mohist/verdict',
    allowedPlacement: 'check',
    mutates: false,
    description: 'Reads a declared output source and verifies a structured PASS/FAIL verdict.',
    inputs: ['outputSource', 'allowedMarkers'],
    outputContract: 'Structured verdict evidence.',
  },
  {
    name: 'mohist/health-gate',
    allowedPlacement: 'check',
    mutates: false,
    description: 'Runs the configured health command as read-only verification evidence for a stage.',
    inputs: ['command', 'timeout', 'autoFix', 'maxFixAttempts'],
    outputContract: 'PASS/FAIL health evidence with command metadata.',
  },
  {
    name: 'mohist/merge-ready',
    allowedPlacement: 'check',
    mutates: false,
    description: 'Checks whether the issue branch can be merged into the target branch.',
    inputs: ['targetBranch', 'strategy'],
    outputContract: 'Mergeability snapshot and conflict metadata.',
  },
  {
    name: 'mohist/ralph-tasks',
    allowedPlacement: 'task',
    mutates: true,
    description: 'Executes generated OpenSpec tasks through Mohist task runtime.',
    inputs: ['tasksPath'],
    outputContract: 'Task completion evidence and artifacts.',
  },
  {
    name: 'mohist/openspec-sync',
    allowedPlacement: 'task',
    mutates: true,
    description: 'Synchronizes OpenSpec change content into project specs.',
    inputs: ['changePath'],
    outputContract: 'Spec sync result.',
  },
  {
    name: 'mohist/archive-change',
    allowedPlacement: 'task',
    mutates: true,
    description: 'Archives an OpenSpec change after delivery.',
    inputs: ['changePath'],
    outputContract: 'Archive path or success metadata.',
  },
  {
    name: 'mohist/merge',
    allowedPlacement: 'task',
    mutates: true,
    description: 'Merges the issue worktree branch into the base branch.',
    inputs: ['strategy', 'targetBranch'],
    outputContract: 'Delivery metadata including landed commit.',
  },
  {
    name: 'mohist/rebase',
    allowedPlacement: 'task',
    mutates: true,
    description: 'Rebases the issue branch onto the latest base branch.',
    inputs: ['targetBranch'],
    outputContract: 'Rebase result and changed snapshot metadata.',
  },
  {
    name: 'mohist/approval',
    allowedPlacement: 'check',
    mutates: false,
    description: 'Waits for explicit user approval using the stage evidence.',
    inputs: ['evidence'],
    outputContract: 'Approval status and response metadata.',
  },
];

export function getWorkflowUseDefinition(name: string): WorkflowUseDefinition | undefined {
  return BUILTIN_WORKFLOW_USES.find(use => use.name === name);
}

export function isWorkflowUseAllowed(name: string, placement: 'task' | 'check'): boolean {
  const use = getWorkflowUseDefinition(name);
  if (!use) return false;
  return use.allowedPlacement === 'both' || use.allowedPlacement === placement;
}

export function inferWorkflowCheckUse(checkName: string): string {
  if (checkName.startsWith('health:')) return 'mohist/health-gate';
  if (checkName === 'review-passed' || checkName === 'self-review-passed') return 'mohist/verdict';
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
