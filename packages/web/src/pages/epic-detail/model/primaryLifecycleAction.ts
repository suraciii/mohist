import { EpicStatus } from '../../../entities/epic'

export type PrimaryLifecycleAction =
  | { kind: 'start-epic' }
  | { kind: 'pause-epic' }
  | { kind: 'resume-epic' }
  | { kind: 'mark-done' }
  | { kind: 'reopen-epic' }

export function primaryLifecycleAction(
  status: EpicStatus,
  readyToMarkDone: boolean,
): PrimaryLifecycleAction | null {
  if (status === EpicStatus.Done || status === EpicStatus.Closed) {
    return { kind: 'reopen-epic' }
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
