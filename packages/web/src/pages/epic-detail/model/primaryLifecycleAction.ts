import { EpicStatus } from '../../../entities/epic/model/types'

export type PrimaryLifecycleAction =
  | { kind: 'start-epic' }
  | { kind: 'pause-epic' }
  | { kind: 'resume-epic' }
  | { kind: 'mark-done' }

export type PrimaryActionKind = PrimaryLifecycleAction['kind']

const PRIMARY_ACTION_ORDER: PrimaryActionKind[] = [
  'start-epic',
  'pause-epic',
  'resume-epic',
  'mark-done',
]

export function primaryLifecycleAction(
  status: EpicStatus,
  readyToMarkDone: boolean,
): PrimaryLifecycleAction | null {
  if (status === EpicStatus.Done || status === EpicStatus.Closed) {
    return null
  }

  if (status === EpicStatus.Paused) {
    return { kind: 'resume-epic' }
  }

  if (readyToMarkDone) {
    return { kind: 'mark-done' }
  }

  if (status === EpicStatus.Idle) {
    return { kind: 'start-epic' }
  }

  if (status === EpicStatus.Running) {
    return { kind: 'pause-epic' }
  }

  return null
}

export function primaryActionKind(action: PrimaryLifecycleAction | null): PrimaryActionKind | null {
  return action?.kind ?? null
}

export function isPrimaryActionKind(value: string): value is PrimaryActionKind {
  return PRIMARY_ACTION_ORDER.includes(value as PrimaryActionKind)
}
