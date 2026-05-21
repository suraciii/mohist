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
  marker?: string;
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
  kind: 'marker';
  required: boolean;
  outputSource: ResultOutputSource;
  allowedMarkers: string[];
  verdicts?: Record<string, WorkflowVerdict>;
  itemPolicy?: {
    blockingSeverities: WorkflowItemSeverity[];
    nonBlockingStatuses: WorkflowItemStatus[];
  };
}

export type ResultOutputSource =
  | { type: 'artifact'; path: string }
  | { type: 'task-output'; key: string };

export interface FailedCheckContext {
  checkName: string;
  verdict: WorkflowVerdict;
  blockingItems: WorkflowItem[];
  nonBlockingItems: WorkflowItem[];
  sourceArtifactRefs?: string[];
  snapshot?: WorkflowSnapshot;
  priorTaskOutputs?: Record<string, unknown>[];
}
