import { statusTreatment, type StatusTreatment } from '@/shared/status-presentation'

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

function buildCategoryStyle(treatment: StatusTreatment, label: string): CategoryStyle {
  return {
    container: treatment.container,
    bg: treatment.container.split(' ')[0]!,
    dot: treatment.dot,
    accentDot: treatment.dot,
    text: treatment.text,
    border: treatment.border,
    label,
  }
}

const CATEGORY_TREATMENTS: Record<TimelineCategory, StatusTreatment> = {
  workflow: statusTreatment('workflow-run', 'running'),
  approval: statusTreatment('approval', 'awaiting'),
  integration: statusTreatment('workflow-run', 'running'),
  success: statusTreatment('workflow-run', 'completed'),
  failure: statusTreatment('severity', 'ERROR'),
  metadata: statusTreatment('severity', 'DEBUG'),
}

export const CATEGORY_STYLES: Record<TimelineCategory, CategoryStyle> = {
  workflow: buildCategoryStyle(CATEGORY_TREATMENTS.workflow, 'Workflow'),
  approval: buildCategoryStyle(CATEGORY_TREATMENTS.approval, 'Approval'),
  integration: buildCategoryStyle(CATEGORY_TREATMENTS.integration, 'Integration'),
  success: buildCategoryStyle(CATEGORY_TREATMENTS.success, 'Success'),
  failure: buildCategoryStyle(CATEGORY_TREATMENTS.failure, 'Failure'),
  metadata: buildCategoryStyle(CATEGORY_TREATMENTS.metadata, 'Metadata'),
}
