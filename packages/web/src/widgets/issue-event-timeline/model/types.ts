import { statusTreatment } from '@/shared/status-presentation'

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
  container: string
  dot: string
  accentDot: string
  bg: string
  text: string
  border: string
  label: string
}

const NEUTRAL_DOT = 'bg-gray-300'
const NEUTRAL_BG = 'bg-muted'
const NEUTRAL_TEXT = 'text-muted-foreground'
const NEUTRAL_BORDER = 'border-border'

const FAILURE_TREATMENT = statusTreatment('severity', 'ERROR')
const ATTENTION_TREATMENT = statusTreatment('severity', 'WARN')

export const CATEGORY_STYLES: Record<TimelineCategory, CategoryStyle> = {
  workflow: {
    container: `${NEUTRAL_BG} ${NEUTRAL_TEXT} ${NEUTRAL_BORDER}`,
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: NEUTRAL_BG,
    text: NEUTRAL_TEXT,
    border: NEUTRAL_BORDER,
    label: 'Workflow',
  },
  approval: {
    container: ATTENTION_TREATMENT.container,
    dot: ATTENTION_TREATMENT.dot,
    accentDot: ATTENTION_TREATMENT.dot,
    bg: ATTENTION_TREATMENT.container,
    text: ATTENTION_TREATMENT.text,
    border: ATTENTION_TREATMENT.border,
    label: 'Approval',
  },
  integration: {
    container: `${NEUTRAL_BG} ${NEUTRAL_TEXT} ${NEUTRAL_BORDER}`,
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: NEUTRAL_BG,
    text: NEUTRAL_TEXT,
    border: NEUTRAL_BORDER,
    label: 'Integration',
  },
  success: {
    container: `${NEUTRAL_BG} ${NEUTRAL_TEXT} ${NEUTRAL_BORDER}`,
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: NEUTRAL_BG,
    text: NEUTRAL_TEXT,
    border: NEUTRAL_BORDER,
    label: 'Success',
  },
  failure: {
    container: FAILURE_TREATMENT.container,
    dot: FAILURE_TREATMENT.dot,
    accentDot: FAILURE_TREATMENT.dot,
    bg: FAILURE_TREATMENT.container,
    text: FAILURE_TREATMENT.text,
    border: FAILURE_TREATMENT.border,
    label: 'Failure',
  },
  metadata: {
    container: `${NEUTRAL_BG} ${NEUTRAL_TEXT} ${NEUTRAL_BORDER}`,
    dot: NEUTRAL_DOT,
    accentDot: NEUTRAL_DOT,
    bg: NEUTRAL_BG,
    text: NEUTRAL_TEXT,
    border: NEUTRAL_BORDER,
    label: 'Metadata',
  },
}
