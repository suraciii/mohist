/**
 * Shared status-presentation layer.
 *
 * One semantic-family source of truth maps every covered domain state to
 * exactly one of `success` / `warning` / `info` / `danger` / `muted`. The
 * resulting family is rendered through `TREATMENT_BY_FAMILY` — the only place
 * that names token utilities for status — yielding a frozen treatment record
 * `{ container, text, border, dot }`.
 *
 * Consumers:
 * - Status pills / badges / dots / markers resolve through `statusTreatment`.
 * - Cross-surface equivalence specs assert on `familyFor`.
 * - The `<StatusPill>` component composes `statusTreatment` with the Badge
 *   primitive for the common pill shape.
 *
 * Design references: D1, D2 (family reservation), D8 (contrast + fixture guard).
 */

import type { Family } from './tokens'

export type SemanticFamily = Family | 'muted'

export type StatusKind =
  | 'issue-health'
  | 'workflow-run'
  | 'workflow-stage'
  | 'approval'
  | 'runner'
  | 'severity'
  | 'context-health'

export interface StatusTreatment {
  readonly container: string
  readonly text: string
  readonly border: string
  readonly dot: string
  readonly family: SemanticFamily
}

type FamilyReservation = Record<string, SemanticFamily>

const ISSUE_HEALTH: FamilyReservation = {
  active: 'info',
  paused: 'muted',
  blocked: 'danger',
  interrupted: 'warning',
  cancelled: 'muted',
  done: 'success',
}

const WORKFLOW_RUN: FamilyReservation = {
  created: 'muted',
  pending: 'muted',
  ready: 'info',
  running: 'info',
  'awaiting-approval': 'warning',
  paused: 'muted',
  stopped: 'muted',
  completed: 'success',
  failed: 'danger',
  drift: 'warning',
}

const WORKFLOW_STAGE: FamilyReservation = {
  pending: 'muted',
  running: 'info',
  'awaiting-approval': 'warning',
  passed: 'success',
  failed: 'danger',
  skipped: 'muted',
  interrupted: 'warning',
  'not-started': 'muted',
}

const APPROVAL: FamilyReservation = {
  pending: 'warning',
  awaiting: 'warning',
  approved: 'success',
  rejected: 'danger',
  error: 'danger',
}

const RUNNER: FamilyReservation = {
  idle: 'success',
  busy: 'info',
  stale: 'warning',
  offline: 'muted',
}

const SEVERITY: FamilyReservation = {
  ERROR: 'danger',
  WARN: 'warning',
  INFO: 'info',
  DEBUG: 'muted',
}

const CONTEXT_HEALTH: FamilyReservation = {
  green: 'success',
  yellow: 'warning',
  red: 'danger',
}

const RESERVATIONS: Record<StatusKind, FamilyReservation> = {
  'issue-health': ISSUE_HEALTH,
  'workflow-run': WORKFLOW_RUN,
  'workflow-stage': WORKFLOW_STAGE,
  approval: APPROVAL,
  runner: RUNNER,
  severity: SEVERITY,
  'context-health': CONTEXT_HEALTH,
}

export function familyFor(kind: StatusKind, state: string | null | undefined): SemanticFamily {
  if (state == null) return 'muted'
  const reservation = RESERVATIONS[kind]
  if (!reservation) return 'muted'
  const family = reservation[state]
  return family ?? 'muted'
}

/**
 * The single place that names token utilities for status surfaces. Every
 * `container`/`text`/`border`/`dot` class is a semantic-token utility; raw
 * Tailwind palette classes (`bg-blue-100`, `text-red-700`, …) are forbidden
 * here so the family meaning stays the source of truth.
 *
 * The text and dot use the family's *base* color (e.g. `text-success`), drawn
 * from the same token family as the background and border so a pill never
 * pairs a success background with a warning dot.
 */
const TREATMENT_BY_FAMILY: Record<SemanticFamily, Omit<StatusTreatment, 'family'>> = Object.freeze({
  success: Object.freeze({
    container: 'bg-success-subtle text-success border-success-border',
    text: 'text-success',
    border: 'border-success-border',
    dot: 'bg-success',
  }),
  warning: Object.freeze({
    container: 'bg-warning-subtle text-warning border-warning-border',
    text: 'text-warning',
    border: 'border-warning-border',
    dot: 'bg-warning',
  }),
  info: Object.freeze({
    container: 'bg-info-subtle text-info border-info-border',
    text: 'text-info',
    border: 'border-info-border',
    dot: 'bg-info',
  }),
  danger: Object.freeze({
    container: 'bg-danger-subtle text-danger border-danger-border',
    text: 'text-danger',
    border: 'border-danger-border',
    dot: 'bg-danger',
  }),
  muted: Object.freeze({
    container: 'bg-muted text-muted-foreground border-border',
    text: 'text-muted-foreground',
    border: 'border-border',
    dot: 'bg-muted-foreground',
  }),
})

export { TREATMENT_BY_FAMILY }

export function statusTreatment(
  kind: StatusKind,
  state: string | null | undefined,
): StatusTreatment {
  const family = familyFor(kind, state)
  const treatment = TREATMENT_BY_FAMILY[family]
  return Object.freeze({ ...treatment, family })
}

/**
 * Test-only export. Allows unit tests to assert that the reservation table
 * covers a documented set of states without re-listing them.
 */
export const RESERVATION_TABLES = RESERVATIONS