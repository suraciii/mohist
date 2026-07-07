import { IssueStatus } from '../../../entities/issue'
import type { SemanticFamily } from '../../../shared/status-presentation'
import { TREATMENT_BY_FAMILY } from '../../../shared/status-presentation'

/**
 * Kanban stage color scheme.
 *
 * Keyed by `IssueStatus` — status-bearing — so it routes through the
 * semantic families (per design D6):
 *
 * - `Backlog`     -> `muted`   (queue, not yet active)
 * - `InProgress`  -> `warning` (active but not done — blocking/warning semantics)
 * - `Done`        -> `success` (terminal completion)
 * - `Cancelled`   -> `danger`  (terminal negative — operator attention)
 *
 * The `accent` value is a token-backed `bg-<family>` class (NOT an inline
 * hex literal) so column accent dots and bars are dark-mode-aware by
 * construction. `labelClass` / `activeBg` / `activeBorder` use the family's
 * soft treatment, also dark-mode-aware.
 *
 * Note: the kanban `InProgress` column is intentionally mapped to `warning`
 * (per task spec), not the generic `info` family used for `workflow-stage.running`.
 * The kanban board groups "things that need attention" visually; the workflow
 * run status is generic and may be in-progress-healthy (`info`). This is the
 * kanban-specific override documented in design D6.
 */

export interface StageColorScheme {
  /** token-backed class for the column accent dot/bar (e.g. `bg-warning`) */
  accent: string
  /** label text color, drawn from the family's text class (e.g. `text-warning`) */
  labelClass: string
  /** background tint when this column is "active" (selected / default) */
  activeBg: string
  /** full-side border color when this column is "active" */
  activeBorder: string
  /** bottom-side border color (used by mobile stage tabs) */
  bottomBorder: string
}

const STAGE_FAMILY: Record<IssueStatus, SemanticFamily> = {
  [IssueStatus.Backlog]: 'muted',
  [IssueStatus.InProgress]: 'warning',
  [IssueStatus.Done]: 'success',
  [IssueStatus.Cancelled]: 'danger',
}

function buildStageColorScheme(family: SemanticFamily): StageColorScheme {
  const treatment = TREATMENT_BY_FAMILY[family]
  const subtleBg = treatment.container.split(' ')[0]!
  return {
    accent: treatment.dot,
    labelClass: treatment.text,
    activeBg: family === 'muted' ? 'bg-muted' : `${subtleBg}/60`,
    activeBorder: treatment.border,
    bottomBorder: family === 'muted' ? 'border-b-border' : `border-b-${family}-border`,
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

/**
 * Test-only export: documents the family reservation for each `IssueStatus`
 * used by the kanban column color scheme. Allows the equivalence spec to
 * assert that the kanban stage palette stays in sync with the documented
 * mapping (Backlog->muted, InProgress->warning, Done->success, Cancelled->danger).
 */
export const STAGE_FAMILY_RESERVATION: Record<IssueStatus, SemanticFamily> = { ...STAGE_FAMILY }