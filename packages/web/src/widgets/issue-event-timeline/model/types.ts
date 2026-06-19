export type TimelineCategory = 'workflow' | 'approval' | 'integration' | 'success' | 'failure' | 'metadata'

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
}

const NEUTRAL_DOT = 'bg-gray-300'

export const CATEGORY_STYLES: Record<TimelineCategory, CategoryStyle> = {
  workflow: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-gray-100',
    text: 'text-gray-600',
    border: 'border-gray-200',
    label: 'Workflow',
  },
  approval: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-gray-100',
    text: 'text-gray-600',
    border: 'border-gray-200',
    label: 'Approval',
  },
  integration: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-gray-100',
    text: 'text-gray-600',
    border: 'border-gray-200',
    label: 'Integration',
  },
  success: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-gray-100',
    text: 'text-gray-600',
    border: 'border-gray-200',
    label: 'Success',
  },
  failure: {
    dot: 'bg-red-500',
    accentDot: 'bg-red-500',
    bg: 'bg-red-50',
    text: 'text-red-700',
    border: 'border-red-200',
    label: 'Failure',
  },
  metadata: {
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: 'bg-gray-100',
    text: 'text-gray-600',
    border: 'border-gray-200',
    label: 'Metadata',
  },
}
