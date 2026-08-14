export type TimelineCategory =
  | 'workflow'
  | 'attention'
  | 'approval'
  | 'integration'
  | 'success'
  | 'failure'
  | 'metadata'

export type TimelineSource = 'ISSUE' | 'WORKFLOW'

export interface TimelineEntry {
  id: string
  type: string
  time: string
  source: TimelineSource
  category: TimelineCategory
  attention: boolean
  description: string
  detail: string | null
  payload: Record<string, unknown>
  isLive: boolean
}

export interface CategoryStyle {
  dot: string
  accentDot: string
  bg: string
  text: string
  border: string
  label: string
  tone: 'success' | 'warning' | 'info' | 'danger' | 'neutral'
}

const NEUTRAL_DOT = 'bg-muted-foreground/60'

export const CATEGORY_STYLES: Record<TimelineCategory, CategoryStyle> = {
  workflow: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-muted',
    text: 'text-muted-foreground',
    border: 'border-border',
    label: 'Workflow',
    tone: 'neutral',
  },
  attention: {
    dot: 'bg-warning',
    accentDot: 'bg-warning',
    bg: 'bg-warning-subtle',
    text: 'text-warning',
    border: 'border-warning-border',
    label: 'Attention',
    tone: 'warning',
  },
  approval: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-warning-subtle',
    text: 'text-warning',
    border: 'border-warning-border',
    label: 'Approval',
    tone: 'warning',
  },
  integration: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-info-subtle',
    text: 'text-info',
    border: 'border-info-border',
    label: 'Integration',
    tone: 'info',
  },
  success: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-success-subtle',
    text: 'text-success',
    border: 'border-success-border',
    label: 'Success',
    tone: 'success',
  },
  failure: {
    dot: 'bg-danger',
    accentDot: 'bg-danger',
    bg: 'bg-danger-subtle',
    text: 'text-danger',
    border: 'border-danger-border',
    label: 'Failure',
    tone: 'danger',
  },
  metadata: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-muted',
    text: 'text-muted-foreground',
    border: 'border-border',
    label: 'Metadata',
    tone: 'neutral',
  },
}
