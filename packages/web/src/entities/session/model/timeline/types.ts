export type TimelineFactSource = 'transcript' | 'live' | 'input' | 'turn' | 'summary' | 'recovery' | 'system'

export type TimelineFactKind =
  | 'input'
  | 'message'
  | 'reasoning'
  | 'tool'
  | 'plan'
  | 'status'
  | 'boundary'
  | 'error'
  | 'suppressed'

export type TimelineToolStatus = 'pending' | 'running' | 'completed' | 'failed' | 'cancelled' | 'timeout'

export type TimelineRenderClass =
  | 'input'
  | 'message'
  | 'reasoning'
  | 'file-read'
  | 'file-edit'
  | 'shell'
  | 'domain-action'
  | 'plan'
  | 'tool'
  | 'status'
  | 'boundary'
  | 'error'
  | 'suppressed'

export type TimelineSalience = 'critical' | 'high' | 'normal' | 'medium' | 'low' | 'quiet'

export interface TimelineFileChange {
  path: string
  operation?: 'created' | 'modified' | 'deleted' | 'moved'
  additions?: number
  deletions?: number
  oldPath?: string
}

export interface TimelineToolFact {
  callId: string
  name: string
  normalizedName?: string
  title?: string
  target?: string
  command?: string
  input?: unknown
  output?: unknown
  status?: TimelineToolStatus
  exitCode?: number
  changedFiles?: TimelineFileChange[]
}

export interface TimelineInputFact {
  text: string
  acceptance?: string
  turnId?: string
}

export interface TimelineStatusFact {
  label?: string
  state?: string
  turnId?: string
}

export interface TimelineBoundaryFact {
  kind: 'reset' | 'compaction' | string
  reason?: string
  summary?: string
}

export interface TimelineErrorFact {
  message?: string
  kind?: string
}

export interface TimelineFact {
  sourceId: string
  source: TimelineFactSource
  order: number
  occurredAt: string
  kind: TimelineFactKind
  raw: unknown
  correlationId?: string
  text?: string
  tool?: TimelineToolFact
  input?: TimelineInputFact
  status?: TimelineStatusFact
  boundary?: TimelineBoundaryFact
  error?: TimelineErrorFact
  groupKey?: string
}

export interface TimelineReference {
  kind: 'issue' | 'agent' | 'workflow'
  label: string
  issueNumber?: number
  agentId?: string
  workflowRunId?: string
}

export interface TimelineDetail {
  input?: unknown
  output?: unknown
  diff?: TimelineFileChange[]
  error?: string
  raw: unknown
}

export interface TimelineItem {
  id: string
  sourceIds: string[]
  occurredAt: string
  renderClass: TimelineRenderClass
  summary: string
  salience: TimelineSalience
  detail?: TimelineDetail
  reference?: TimelineReference
  groupKey?: string
  isTerminal: boolean
  isStreaming?: boolean
}

export interface TimelineGroup {
  id: string
  renderClass: 'file-read' | 'shell' | 'tool'
  sourceIds: string[]
  summary: string
  salience: TimelineSalience
  items: TimelineItem[]
}

export type TimelineEntry = TimelineItem | TimelineGroup

export function isTimelineGroup(entry: TimelineEntry): entry is TimelineGroup {
  return 'items' in entry
}
