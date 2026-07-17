import { http, HttpResponse } from 'msw'
import type { EpicDetail } from '../../../entities/epic'
import type { Issue } from '../../../entities/issue'
import { server, useMswServer } from '../../../../tests/support/msw'

const EPICS = '*/api/projects/:projectId/epics/:number'
const ISSUES = '*/api/projects/:projectId/issues'

let epicData: EpicDetail = {} as EpicDetail
let issuesData: Issue[] = []

const baseHandlers = [
  http.get(EPICS, () => HttpResponse.json({ success: true, data: epicData })),
  http.get(`${EPICS}/events`, () => HttpResponse.json({ success: true, data: [] })),
  http.get(ISSUES, () => HttpResponse.json({ success: true, data: issuesData })),
] as const

export function mountEpicDetail(epic: EpicDetail, issues: readonly Record<string, unknown>[] = []) {
  epicData = epic
  issuesData = issues as unknown as Issue[]
  useMswServer(...baseHandlers)
}

export function mockEpic(epic: EpicDetail) {
  epicData = epic
  server.use(http.get(EPICS, () => HttpResponse.json({ success: true, data: epic })))
}

export function mockEpicIssues(issues: Issue[]) {
  issuesData = issues
  server.use(http.get(ISSUES, () => HttpResponse.json({ success: true, data: issues })))
}

export { server }
