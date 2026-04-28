export enum Stage {
  Explore = 'explore',
  Plan = 'plan',
  Build = 'build',
  Review = 'review',
  Done = 'done',
  Draft = 'draft'
}

export const STAGE_ORDER: Stage[] = [
  Stage.Explore,
  Stage.Plan,
  Stage.Build,
  Stage.Review,
  Stage.Done
];

export const STAGE_TRANSITIONS: Record<Stage, Stage[]> = {
  [Stage.Explore]: [Stage.Plan],
  [Stage.Plan]: [Stage.Build],
  [Stage.Build]: [Stage.Review],
  [Stage.Review]: [Stage.Done, Stage.Build],
  [Stage.Done]: [],
  [Stage.Draft]: [Stage.Plan]
};

export function isValidTransition(from: Stage, to: Stage): boolean {
  const allowed = STAGE_TRANSITIONS[from];
  return allowed?.includes(to) ?? false;
}

export enum IssueStatus {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked',
  Interrupted = 'interrupted',
  Closed = 'closed',
  Completed = 'completed'
}

export type Priority = 'p0' | 'p1' | 'p2' | 'p3' | 'p4';

export const VALID_PRIORITIES: Priority[] = ['p0', 'p1', 'p2', 'p3', 'p4'];

export type ApprovalStatus = 'pending' | 'awaiting' | 'approved' | 'rejected' | 'error';

export enum MergeState {
  Pending = 'pending',
  Rebasing = 'rebasing',
  Merging = 'merging',
  Merged = 'merged',
  BuildFailed = 'build-failed',
  Conflict = 'conflict',
  Resolving = 'resolving',
  Blocked = 'blocked',
}

export interface ApprovalState {
  stage: Stage;
  status: ApprovalStatus;
  output: unknown;
  requestedAt: string;
  respondedAt?: string;
}

export interface Issue {
  id: string;
  number: number;
  title: string;
  body?: string;
  stage: Stage;
  status: IssueStatus;
  projectId: string;
  labels: string[];
  priority: Priority;
  createdAt: string;
  updatedAt: string;
  approvalState?: ApprovalState;
  mergeState?: MergeState;
}

export interface Project {
  id: string;
  name: string;
  path: string;
  baseBranch: string;
  createdAt: string;
  updatedAt: string;
}

export interface Config {
  serverPort: number;
  serverHost: string;
  pollInterval: number;
  maxConcurrentAgents: number;
  agentTimeout: number;
}

export interface ServerState {
  isRunning: boolean;
  pid?: number;
  port: number;
  startedAt?: string;
}

export interface ApiResponse<T = any> {
  success: boolean;
  data?: T;
  error?: string;
}

export interface Comment {
  id: string;
  issueId: string;
  body: string;
  createdAt: string;
}

export type QuestionStatus = 'pending' | 'answered' | 'expired';

export interface Question {
  id: string;
  issueId: string;
  question: string;
  answer?: string;
  status: QuestionStatus;
  createdAt: string;
  answeredAt?: string;
}

export enum ExploreStatus {
  Active = 'active',
  Crystallized = 'crystallized',
  Archived = 'archived',
}

export interface ExploreSession {
  id: string;
  projectId: string;
  issueId: string | null;
  issueNumber?: number;
  title: string;
  status: ExploreStatus;
  model?: string;
  variant?: string;
  createdAt: string;
  updatedAt: string;
}

export interface ToolCallRecord {
  name: string;
  args: Record<string, unknown>;
  result: unknown;
}

export class ConfigConflictError extends Error {
  currentVersion: number;
  expectedVersion: number;

  constructor(currentVersion: number, expectedVersion: number) {
    super(`Config version conflict: expected ${expectedVersion} but current is ${currentVersion}`);
    this.name = 'ConfigConflictError';
    this.currentVersion = currentVersion;
    this.expectedVersion = expectedVersion;
  }
}

export interface ExploreMessage {
  id: string;
  sessionId: string;
  role: 'user' | 'assistant';
  content: string;
  toolCalls: ToolCallRecord[] | null;
  createdAt: string;
}
