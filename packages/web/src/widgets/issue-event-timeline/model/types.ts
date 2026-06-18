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
  bg: string
  text: string
  border: string
  label: string
}

export const CATEGORY_STYLES: Record<TimelineCategory, CategoryStyle> = {
  workflow: {
    dot: 'bg-blue-500',
    bg: 'bg-blue-50',
    text: 'text-blue-700',
    border: 'border-blue-200',
    label: 'Workflow',
  },
  approval: {
    dot: 'bg-amber-500',
    bg: 'bg-amber-50',
    text: 'text-amber-700',
    border: 'border-amber-200',
    label: 'Approval',
  },
  integration: {
    dot: 'bg-purple-500',
    bg: 'bg-purple-50',
    text: 'text-purple-700',
    border: 'border-purple-200',
    label: 'Integration',
  },
  success: {
    dot: 'bg-green-500',
    bg: 'bg-green-50',
    text: 'text-green-700',
    border: 'border-green-200',
    label: 'Success',
  },
  failure: {
    dot: 'bg-red-500',
    bg: 'bg-red-50',
    text: 'text-red-700',
    border: 'border-red-200',
    label: 'Failure',
  },
  metadata: {
    dot: 'bg-gray-400',
    bg: 'bg-gray-100',
    text: 'text-gray-700',
    border: 'border-gray-200',
    label: 'Metadata',
  },
}
