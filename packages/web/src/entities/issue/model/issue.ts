import type { ApprovalState, ApprovalFeedback } from './approval'
import type { BaseDriftInfo } from './drift'
import type { RecoveryProjection, WorkflowConvergenceState } from './recovery'

export enum IssueStatus {
  Backlog = 'backlog',
  InProgress = 'in_progress',
  Done = 'done',
  Cancelled = 'cancelled',
}

export enum WorkflowStage {
  Plan = 'plan',
  Build = 'build',
  Check = 'check',
  Integrate = 'integrate',
  Done = 'done',
}

export const STATUS_ORDER: IssueStatus[] = [
  IssueStatus.Backlog,
  IssueStatus.InProgress,
  IssueStatus.Done,
  IssueStatus.Cancelled,
]

export const WORKFLOW_STAGE_ORDER: WorkflowStage[] = [
  WorkflowStage.Plan,
  WorkflowStage.Build,
  WorkflowStage.Check,
  WorkflowStage.Integrate,
  WorkflowStage.Done,
]

export enum IssueHealth {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked',
  Cancelled = 'cancelled',
  Done = 'done',
}

export interface Comment {
  id: string
  author: string | null
  body: string
  createdAt: string
  attachments?: AttachmentInfo[]
}

export interface AttachmentInfo {
  id: string
  fileName: string
  contentType: string
  size: number
}

export interface IssuePrerequisiteSummary {
  number: number
  title: string
  completed: boolean
  stage?: string
  status: IssueStatus
  health: IssueHealth
}

export interface IssueParentRef {
  number: number
  title: string
}

export interface ChildIssuesSummary {
  hasChildren: boolean
  count: number
  backlogCount: number
  inProgressCount: number
  doneCount: number
  cancelledCount: number
  blockedCount: number
}

export interface IssueChildRef {
  number: number
  title: string
  status: IssueStatus
  health: IssueHealth
  repositoryName: string | null
}

export type IssueStartBlocker =
  | { kind: 'draft' }
  | { kind: 'waiting-for'; issue: { number: number; title: string; stage?: string; status?: string } }

export interface WorkflowStageProgress {
  stage: string
  total: number
  completed: number
  running: number
  failed: number
  currentTaskTitle?: string | null
}

export interface Issue {
  number: number
  title: string
  body?: string
  status: IssueStatus
  workflowStage?: WorkflowStage | null
  workflowStatus?: string | null
  workflowStageProgress?: WorkflowStageProgress | null
  workflowRunId?: string | null
  workflowProfileId?: string | null
  health: IssueHealth
  projectId: string
  labels: Record<string, string>
  createdAt: string
  updatedAt: string
  projectName?: string
  repository?: { name: string; gitUrl: string; baseBranch: string } | null
  comments?: Comment[]
  attachments?: AttachmentInfo[]
  approvalState?: ApprovalState
  priority?: string | null
  risk?: string | null
  model?: string | null
  modelVariant?: string | null
  agentConfig?: Record<string, unknown> | null
  stageModels?: Record<string, string> | null
  stageModelVariants?: Record<string, string> | null
  completedAt?: string
  archivedAt?: string
  blockedReason?: string
  prerequisiteNumbers?: number[]
  prerequisites?: IssuePrerequisiteSummary[]
  isDraft: boolean
  canStart: boolean
  canBeParent?: boolean
  blocker: IssueStartBlocker | null
  drift?: BaseDriftInfo | null
  primaryEpic?: { number: number | null; title: string; status: string; priority: string } | null
  parentIssueRef?: IssueParentRef | null
  childIssuesSummary?: ChildIssuesSummary | null
  children?: IssueChildRef[]
  repositoryName?: string | null
  recovery?: RecoveryProjection | null
  convergence?: WorkflowConvergenceState | null
  feedback?: ApprovalFeedback[] | null
}

// === Issue-entity API response shapes ===

export interface IssueWorkflowProfileYamlResponse {
  issueNumber: number
  projectId: string
  sourceTemplateId?: string | null
  hasCustomTemplate: boolean
  yaml: string | null
  workflowRunId: string | null
  profileId: string
  updateMode: string
  variables: unknown
  updatedAt: string
  templateSource?: 'system' | 'project' | 'custom'
}

export interface StoredCloudEventDto {
  id: number
  eventId: string
  source: string
  type: string
  specVersion: string
  subject: string | null
  time: string
  dataContentType: string | null
  data: unknown
  extensions: Record<string, string>
}
