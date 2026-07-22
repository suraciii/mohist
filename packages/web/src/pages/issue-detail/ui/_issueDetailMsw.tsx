import { HttpResponse, http } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'

const ISSUES = '*/api/projects/:projectId/issues/:number'
const AGENT_STATUS = '*/api/projects/:projectId/agent/status'
const OPENCODE_MODELS = '*/api/projects/:projectId/opencode/models'
const WORKFLOW_VARIABLES = '*/api/projects/:projectId/workflow-profile/variables'
const PROJECT_DEFAULT = '*/api/projects/:projectId/workflow-profile'
const SYSTEM_PROFILES = '*/api/workflow-templates/system*'
const RUN_YAML = '*/api/workflow-runs/:runId/yaml'

let currentIssue: Record<string, unknown> | null = null

export interface IssueDetailFixture {
  issue: Record<string, unknown>
}

function issueDetailHandlers({ issue }: IssueDetailFixture) {
  return [
    http.get(ISSUES, () => HttpResponse.json({ success: true, data: issue })),
    http.get('*/api/projects/:projectId/issues', () =>
      HttpResponse.json({ success: true, data: [] }),
    ),
    http.get(`${ISSUES}/diff`, () =>
      HttpResponse.json({
        success: true,
        data: { available: false, reason: 'not_started', message: 'no workspace' },
      }),
    ),
    http.get(`${ISSUES}/commits`, () =>
      HttpResponse.json({
        success: true,
        data: { available: false, reason: 'not_started', message: 'no workspace' },
      }),
    ),
    http.get(`${ISSUES}/workflow/status`, () =>
      HttpResponse.json({ success: true, data: { workflow: null } }),
    ),
    http.get(`${ISSUES}/workspace-status`, () =>
      HttpResponse.json({ success: true, data: { exists: false, reason: 'not_started' } }),
    ),
    http.get(`${ISSUES}/events`, () => HttpResponse.json({ success: true, data: [] })),
    http.get(`${ISSUES}/workflow/artifacts`, () => HttpResponse.json({ success: true, data: [] })),
    http.get(`${ISSUES}/workflow/tasks/:taskId/logs`, () =>
      HttpResponse.json({ success: true, data: { lines: [], nextCursor: null, truncated: false } }),
    ),
    http.get(`${ISSUES}/workflow-profile/variables`, () =>
      HttpResponse.json({ success: true, data: { vars: {}, stages: {} } }),
    ),
    http.get(`${ISSUES}/workflow-profile`, () =>
      HttpResponse.json({
        success: true,
        data: {
          issueNumber: 14,
          projectId: 'proj-1',
          hasCustomTemplate: false,
          yaml: null,
          workflowRunId: null,
          profileId: '',
        },
      }),
    ),
    http.get(AGENT_STATUS, () =>
      HttpResponse.json({
        success: true,
        data: {
          running: false,
          issueNumber: null,
          activeAgents: [],
          runnerAvailable: true,
          capacity: { active: 0, max: 1 },
        },
      }),
    ),
    http.get(OPENCODE_MODELS, () =>
      HttpResponse.json({ success: true, data: { models: [], modelVariants: {} } }),
    ),
    http.get(WORKFLOW_VARIABLES, () =>
      HttpResponse.json({ success: true, data: { vars: {}, stages: {} } }),
    ),
    http.get(PROJECT_DEFAULT, () =>
      HttpResponse.json({
        success: true,
        data: {
          projectId: 'proj-1',
          defaultTemplateId: null,
          disabledWorkflowProfileIds: [],
        },
      }),
    ),
    http.get(SYSTEM_PROFILES, () => HttpResponse.json({ success: true, data: [] })),
    http.get(RUN_YAML, () =>
      HttpResponse.json({ success: true, data: { workflowRunId: 'unused', yaml: '' } }),
    ),
    http.get('*/api/workflow-runs/:runId/sessions', () =>
      HttpResponse.json({ success: true, data: [] }),
    ),
    http.patch(ISSUES, () =>
      HttpResponse.json({ success: true, data: { isDraft: false } }),
    ),
    http.post(`${ISSUES}/start`, () =>
      HttpResponse.json({ success: true, data: { issue: {}, message: '' } }),
    ),
  ]
}

export function mountIssueDetail(fixture: IssueDetailFixture) {
  currentIssue = fixture.issue
  useMswServer(...issueDetailHandlers(fixture))
}

export function mockIssue(issue: Record<string, unknown>) {
  currentIssue = issue
  server.use(http.get(ISSUES, () => HttpResponse.json({ success: true, data: issue })))
}

export function mockIssueError(status: number, message = 'Issue transport failed') {
  currentIssue = null
  server.use(
    http.get(ISSUES, () =>
      HttpResponse.json({ success: false, error: message, code: status === 404 ? 'not_found' : 'transport_error' }, { status }),
    ),
  )
}

export function mockIssuePending() {
  let resolve: ((response: Response) => void) | undefined
  server.use(
    http.get(ISSUES, () => new Promise<Response>((done) => {
      resolve = done
    })),
  )
  return (issue: Record<string, unknown>) => {
    resolve?.(HttpResponse.json({ success: true, data: issue }))
  }
}

export function getCurrentIssueFixture() {
  return currentIssue
}

export function mockIssueDiff(diff: Record<string, unknown> | null) {
  server.use(
    http.get(`${ISSUES}/diff`, () =>
      HttpResponse.json({ success: true, data: diff ?? { available: false } }),
    ),
  )
}

export function mockIssueDiffError(status = 503) {
  server.use(
    http.get(`${ISSUES}/diff`, () =>
      HttpResponse.json({ success: false, error: 'Diff transport failed' }, { status }),
    ),
  )
}

export function mockIssueDiffPending() {
  let resolve: ((response: Response) => void) | undefined
  server.use(
    http.get(`${ISSUES}/diff`, () => new Promise<Response>((done) => {
      resolve = done
    })),
  )
  return (diff: Record<string, unknown>) => {
    resolve?.(HttpResponse.json({ success: true, data: diff }))
  }
}

export function mockIssueCommits(commits: Record<string, unknown> | null) {
  server.use(
    http.get(`${ISSUES}/commits`, () =>
      HttpResponse.json({ success: true, data: commits ?? { available: false } }),
    ),
  )
}

export function mockWorkflowTimeline(timeline: Record<string, unknown> | null) {
  server.use(
    http.get(`${ISSUES}/workflow/status`, () =>
      HttpResponse.json({ success: true, data: { workflow: timeline } }),
    ),
  )
}

export function mockWorkspaceStatus(status: Record<string, unknown> | null) {
  server.use(
    http.get(`${ISSUES}/workspace-status`, () =>
      HttpResponse.json({ success: true, data: status ?? { exists: false } }),
    ),
  )
}

export function mockArtifacts(artifacts: Array<Record<string, unknown>>) {
  server.use(
    http.get(`${ISSUES}/workflow/artifacts`, () =>
      HttpResponse.json({ success: true, data: artifacts }),
    ),
  )
}

export function mockArtifactsError(status = 503) {
  server.use(
    http.get(`${ISSUES}/workflow/artifacts`, () =>
      HttpResponse.json({ success: false, error: 'Artifact transport failed' }, { status }),
    ),
  )
}

export function mockArtifactContent(artifactId: string, content: string, contentType = 'text/markdown') {
  server.use(
    http.get(`${ISSUES}/workflow/artifacts/${artifactId}/content`, () =>
      new HttpResponse(content, { headers: { 'content-type': contentType } }),
    ),
  )
}

export function mockAgentStatus(status: Record<string, unknown>) {
  server.use(http.get(AGENT_STATUS, () => HttpResponse.json({ success: true, data: status })))
}

export function mockWorkflowRunSessions(sessions: Array<Record<string, unknown>>) {
  server.use(
    http.get('*/api/workflow-runs/:runId/sessions', () =>
      HttpResponse.json({ success: true, data: sessions }),
    ),
  )
}

export function mockUpdateIssue(handler: (info: { request: Request }) => Promise<Response> | Response) {
  server.use(http.patch(ISSUES, handler as any))
}
