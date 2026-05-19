import type { TasksFile } from '../artifacts/change-artifacts-manager';

export type WorkflowVerdict = 'PASS' | 'FAIL';

export type WorkflowItemSeverity = 'blocking' | 'warning' | 'follow-up' | 'info';

export type WorkflowItemStatus = 'open' | 'resolved' | 'unresolved' | 'pre-existing' | 'out-of-scope';

export interface WorkflowItem {
  id: string;
  severity: WorkflowItemSeverity;
  status?: WorkflowItemStatus;
  scope?: string;
  evidence: string;
  suggestedAction?: string;
  verification?: string;
}

export interface WorkflowVerification {
  checkName: string;
  status: 'pass' | 'fail' | 'error' | 'pending';
  command: string;
  duration: number;
  summary: string;
  logExcerpt: string;
  checkedAt: string;
  candidateHeadSha?: string;
  baseSha?: string;
}

export interface WorkflowSnapshot {
  sha?: string;
  changedFiles?: string[];
  diffStat?: string;
}

export interface StructuredWorkflowResult {
  verdict?: WorkflowVerdict;
  marker?: '<promise>PASS</promise>' | '<promise>FAIL</promise>';
  items?: WorkflowItem[];
  evidence?: string;
  repairedItemIds?: string[];
  verification?: WorkflowVerification[];
  snapshot?: WorkflowSnapshot;
  summary?: string;
  facts?: Record<string, unknown>;
}

export interface ReactionTaskOutput extends StructuredWorkflowResult {
  attemptedItemIds: string[];
  resolvedItemIds: string[];
  unresolvedItemIds: string[];
  newItemIds?: string[];
}

export interface ResultContract {
  kind: 'promise-marker';
  required: boolean;
  outputSource: ResultOutputSource;
  allowedMarkers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'];
  itemPolicy?: {
    blockingSeverities: WorkflowItemSeverity[];
    nonBlockingStatuses: WorkflowItemStatus[];
  };
}

export type ResultOutputSource =
  | { type: 'artifact'; path: string }
  | { type: 'task-output'; key: string };

export interface SelfRepairPolicy {
  enabled: boolean;
  allowedScopes: string[];
  maxAttempts?: number;
  requiresVerification: boolean;
  disallowedReasons: string[];
}

export interface WorkflowConvergenceState {
  failedCheck?: string;
  blockingItemCount: number;
  directlyRepairedCount: number;
  reactionAttempts: number;
  attemptedItemIds: string[];
  resolvedItemIds: string[];
  unresolvedItemIds: string[];
  newBlockingItemIds: string[];
  nonBlockingItemIds: string[];
  blockedReason?: string;
}

export interface FailedCheckContext {
  checkName: string;
  verdict: WorkflowVerdict;
  blockingItems: WorkflowItem[];
  nonBlockingItems: WorkflowItem[];
  sourceArtifactRefs?: string[];
  snapshot?: WorkflowSnapshot;
  priorTaskOutputs?: Record<string, unknown>[];
}

export interface ReviewIssue {
  severity: 'error' | 'warning';
  location: string;
  message: string;
  suggestion?: string;
}

export interface DimensionResult {
  name: string;
  passed: boolean;
  reasoning: string;
  issues?: ReviewIssue[];
}

export interface ReviewResult {
  passed: boolean;
  dimensions: DimensionResult[];
  overallReasoning: string;
  duration: number;
  fixSuggestions?: string[];
}

export interface PlanResult {
  success: boolean;
  changePath: string;
  artifacts: {
    proposal: string;
    design: string;
    specs: Array<{ name: string; content: string }>;
    tasks: TasksFile | null;
  };
  iterations: number;
  duration: number;
  selfReviewNotes?: string;
  error?: string;
}