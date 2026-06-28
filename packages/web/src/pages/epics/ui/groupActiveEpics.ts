import type { EpicWithProgress } from '../../../entities/epic'

export interface ActiveEpicGroups {
  running: EpicWithProgress[]
  readyToStart: EpicWithProgress[]
  waitingBlocked: EpicWithProgress[]
  idleEmpty: EpicWithProgress[]
}

export function groupActiveEpics(epics: EpicWithProgress[]): ActiveEpicGroups {
  const running: EpicWithProgress[] = []
  const readyToStart: EpicWithProgress[] = []
  const waitingBlocked: EpicWithProgress[] = []
  const idleEmpty: EpicWithProgress[] = []

  for (const epic of epics) {
    const progress = epic.progress
    if (progress.activeIssues.length > 0) {
      running.push(epic)
    } else if (progress.nextIssue != null) {
      readyToStart.push(epic)
    } else if (progress.nextIssueReason != null) {
      waitingBlocked.push(epic)
    } else {
      idleEmpty.push(epic)
    }
  }

  return { running, readyToStart, waitingBlocked, idleEmpty }
}