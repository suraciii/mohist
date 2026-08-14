export const NOTIFICATION_KINDS = {
  WorkflowFailed: 'workflow_failed',
  AgentResultUnconfirmed: 'agent_result_unconfirmed',
  ApprovalRequested: 'approval_requested',
  IssueStarted: 'issue_started',
  IssueCompleted: 'issue_completed',
} as const

export type NotificationKind = (typeof NOTIFICATION_KINDS)[keyof typeof NOTIFICATION_KINDS]

export const NOTIFICATION_KIND_VALUES: readonly NotificationKind[] = [
  NOTIFICATION_KINDS.WorkflowFailed,
  NOTIFICATION_KINDS.AgentResultUnconfirmed,
  NOTIFICATION_KINDS.ApprovalRequested,
  NOTIFICATION_KINDS.IssueStarted,
  NOTIFICATION_KINDS.IssueCompleted,
]

export function isNotificationKind(value: string): value is NotificationKind {
  return (NOTIFICATION_KIND_VALUES as readonly string[]).includes(value)
}

export function parseNotificationKind(value: string | null | undefined): NotificationKind {
  if (value && isNotificationKind(value)) {
    return value
  }
  return NOTIFICATION_KINDS.WorkflowFailed
}

export interface InboxSubscription {
  workflow_failed: boolean
  agent_result_unconfirmed?: boolean
  approval_requested: boolean
  issue_started: boolean
  issue_completed: boolean
}

export type InboxSubscriptionUpdate = InboxSubscription

export interface InboxSubscriptionApiData {
  data: InboxSubscription
}

export interface InboxSubscriptionResponse {
  success: boolean
  data: InboxSubscription
}

export interface InboxItem {
  itemId: string
  notificationKind: NotificationKind
  issueNumber: number
  issueTitle: string
  createdAt: string
  readAt?: string | null
  archivedAt?: string | null
  isRead: boolean
  isArchived: boolean
}

export interface InboxMarkReadResponse {
  itemId: string
  read: true
}

export interface InboxArchiveResponse {
  itemId: string
  archived: true
}

export interface InboxMarkAllReadResponse {
  projectId: string
  marked: number
}

export interface InboxUnreadCount {
  unreadCount: number
}
