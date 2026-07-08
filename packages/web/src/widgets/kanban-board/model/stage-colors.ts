import { IssueStatus } from '../../../entities/issue'
import type { SemanticFamily } from '../../../shared/status-presentation'
import { TREATMENT_BY_FAMILY } from '../../../shared/status-presentation'

export interface StageColorScheme {
  accent: string
  labelClass: string
  activeBg: string
  activeBorder: string
  bottomBorder: string
}

const STAGE_FAMILY: Record<IssueStatus, SemanticFamily> = {
  [IssueStatus.Backlog]: 'muted',
  [IssueStatus.InProgress]: 'info',
  [IssueStatus.Done]: 'success',
  [IssueStatus.Cancelled]: 'muted',
}

const BOTTOM_BORDER_BY_FAMILY: Record<SemanticFamily, string> = {
  success: 'border-b-success-border',
  warning: 'border-b-warning-border',
  info: 'border-b-info-border',
  danger: 'border-b-danger-border',
  muted: 'border-b-border',
}

function buildStageColorScheme(family: SemanticFamily): StageColorScheme {
  const treatment = TREATMENT_BY_FAMILY[family]
  const subtleBg = treatment.container.split(' ')[0]!
  return {
    accent: treatment.dot,
    labelClass: treatment.text,
    activeBg: family === 'muted' ? 'bg-muted' : `${subtleBg}/60`,
    activeBorder: treatment.border,
    bottomBorder: BOTTOM_BORDER_BY_FAMILY[family],
  }
}

export const STAGE_COLORS: Record<IssueStatus, StageColorScheme> = {
  [IssueStatus.Backlog]: buildStageColorScheme(STAGE_FAMILY[IssueStatus.Backlog]),
  [IssueStatus.InProgress]: buildStageColorScheme(STAGE_FAMILY[IssueStatus.InProgress]),
  [IssueStatus.Done]: buildStageColorScheme(STAGE_FAMILY[IssueStatus.Done]),
  [IssueStatus.Cancelled]: buildStageColorScheme(STAGE_FAMILY[IssueStatus.Cancelled]),
}

export function getStageColors(status: IssueStatus): StageColorScheme {
  return STAGE_COLORS[status] ?? STAGE_COLORS[IssueStatus.Backlog]
}

export const STAGE_FAMILY_RESERVATION: Record<IssueStatus, SemanticFamily> = { ...STAGE_FAMILY }
