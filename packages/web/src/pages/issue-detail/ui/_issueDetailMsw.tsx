import { HttpResponse, http } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'

const ISSUES = '*/api/projects/:projectId/issues/:number'
const AGENT_STATUS = '*/api/projects/:projectId/agent/status'
const OPENCODE_MODELS = '*/api/projects/:projectId/opencode/models'
const WORKFLOW_VARIABLES = '*/api/projects/:projectId/variables'
const PROJECT_DEFAULT = '*/api/projects/:projectId/workflow-profile/default'
const SYSTEM_PROFILES = '*/api/projects/:projectId/workflow-profiles'
const RUN_YAML = '*/api/workflow-runs/:runId/yaml'
const AGENTS_BY_NAME = '*/api/projects/:projectId/agents/by-name/*'

/** A Profile Definition with two named Agent tasks, in the built-in shape. */
export const NAMED_AGENTS_DEFINITION_SOURCE = [
  'id: mohist/local',
  'stages:',
  '- stage: plan',
  '  tasks:',
  '  - id: plan',
  '    title: Plan the change',
  '    uses: mohist/agent',
  '    with:',
  '      name: mohist/planner',
  '      session: plan',
  '- stage: build',
  '  tasks:',
  '  - id: build',
  '    uses: mohist/agent',
  '    with:',
  '      name: mohist/builder',
].join('\n')

let currentIssue: Record<string, unknown> | null = null

export interface IssueDetailFixture {
  issue: Record<string, unknown>
}

interface ProfileDetailFacts {
  displayName?: string
  description?: string
  isDefault?: boolean
}

/**
 * Profile-detail read for a built-in Profile whose tasks use named Agents.
 * Profile ids contain a path separator, so the read goes through a wildcard.
 */
export function namedAgentsProfileDetailHandler(
  facts: (profileId: string) => ProfileDetailFacts = () => ({}),
  stages: Array<Record<string, unknown>> = [],
) {
  return http.get(`${SYSTEM_PROFILES}/*`, ({ request }) => {
    const profileId = decodeURIComponent(new URL(request.url).pathname.split('/workflow-profiles/')[1] ?? '')
    const resolved = facts(profileId)
    return HttpResponse.json({
      success: true,
      data: {
        projectId: 'proj-1',
        profileId,
        name: resolved.displayName ?? profileId,
        displayName: resolved.displayName ?? profileId,
        description: resolved.description ?? '',
        isDefault: resolved.isDefault ?? false,
        sourceProvenance: 'BuiltIn',
        isBuiltIn: true,
        definitionSource: NAMED_AGENTS_DEFINITION_SOURCE,
        yaml: NAMED_AGENTS_DEFINITION_SOURCE,
        stages,
      },
    })
  })
}

/** By-name read answering every named Agent with its built-in definition. */
export function agentsByNameHandler(projectId = 'proj-1') {
  return http.get(AGENTS_BY_NAME, ({ params }) => {
    // The leading wildcard in the handler path owns params[0]; the Agent name is params[1].
    const name = String(params[1] ?? '').replace(/^\/+/, '')
    return HttpResponse.json({
      success: true,
      data: {
        id: `builtin:${name}`,
        projectId,
        name,
        purpose: null,
        description: '',
        instructions: '',
        agentConfig: null,
        skills: [],
        permissions: [],
        maxConcurrentRuns: null,
        status: 'active',
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        origin: 'built-in',
      },
    })
  })
}

function issueDetailHandlers({ issue }: IssueDetailFixture) {
  return [
    http.get(ISSUES, () => HttpResponse.json({ success: true, data: issue })),
    http.get('*/api/projects/:projectId/issues', () => HttpResponse.json({ success: true, data: [] })),
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
    http.get(`${ISSUES}/workflow/status`, () => HttpResponse.json({ success: true, data: { workflow: null } })),
    http.get(`${ISSUES}/workspace-status`, () =>
      HttpResponse.json({ success: true, data: { exists: false, reason: 'not_started' } }),
    ),
    http.get(`${ISSUES}/events`, () => HttpResponse.json({ success: true, data: [] })),
    http.get(`${ISSUES}/workflow/artifacts`, () => HttpResponse.json({ success: true, data: [] })),
    http.get(`${ISSUES}/workflow/tasks/:taskId/logs`, () =>
      HttpResponse.json({ success: true, data: { lines: [], nextCursor: null, truncated: false } }),
    ),
    http.get(`${ISSUES}/variables`, () => HttpResponse.json({ success: true, data: { vars: {}, stages: {} } })),
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
    http.get(OPENCODE_MODELS, () => HttpResponse.json({ success: true, data: { models: [], modelVariants: {} } })),
    http.get(WORKFLOW_VARIABLES, () => HttpResponse.json({ success: true, data: { vars: {}, stages: {} } })),
    http.get(PROJECT_DEFAULT, () =>
      HttpResponse.json({
        success: true,
        data: {
          projectId: 'proj-1',
          defaultWorkflowProfileId: 'mohist/local',
          disabledWorkflowProfileIds: [],
        },
      }),
    ),
    http.get(SYSTEM_PROFILES, () => HttpResponse.json({ success: true, data: [] })),
    namedAgentsProfileDetailHandler(
      () => ({ isDefault: true }),
      [
        { stage: 'plan', displayName: 'Plan' },
        { stage: 'build', displayName: 'Build' },
      ],
    ),
    agentsByNameHandler(),
    http.get(RUN_YAML, () => HttpResponse.json({ success: true, data: { workflowRunId: 'unused', yaml: '' } })),
    http.get('*/api/workflow-runs/:runId/sessions', () => HttpResponse.json({ success: true, data: [] })),
    http.patch(ISSUES, () => HttpResponse.json({ success: true, data: { isDraft: false } })),
    http.post(`${ISSUES}/start`, () => HttpResponse.json({ success: true, data: { issue: {}, message: '' } })),
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
      HttpResponse.json(
        { success: false, error: message, code: status === 404 ? 'not_found' : 'transport_error' },
        { status },
      ),
    ),
  )
}

export function mockIssuePending() {
  let resolve: ((response: Response) => void) | undefined
  server.use(
    http.get(
      ISSUES,
      () =>
        new Promise<Response>((done) => {
          resolve = done
        }),
    ),
  )
  return (issue: Record<string, unknown>) => {
    resolve?.(HttpResponse.json({ success: true, data: issue }))
  }
}

export function getCurrentIssueFixture() {
  return currentIssue
}

export function mockIssueDiff(diff: Record<string, unknown> | null) {
  server.use(http.get(`${ISSUES}/diff`, () => HttpResponse.json({ success: true, data: diff ?? { available: false } })))
}

export function mockIssueDiffError(status = 503) {
  server.use(
    http.get(`${ISSUES}/diff`, () => HttpResponse.json({ success: false, error: 'Diff transport failed' }, { status })),
  )
}

export function mockIssueDiffPending() {
  let resolve: ((response: Response) => void) | undefined
  server.use(
    http.get(
      `${ISSUES}/diff`,
      () =>
        new Promise<Response>((done) => {
          resolve = done
        }),
    ),
  )
  return (diff: Record<string, unknown>) => {
    resolve?.(HttpResponse.json({ success: true, data: diff }))
  }
}

export function mockIssueCommits(commits: Record<string, unknown> | null) {
  server.use(
    http.get(`${ISSUES}/commits`, () => HttpResponse.json({ success: true, data: commits ?? { available: false } })),
  )
}

export function mockWorkflowTimeline(timeline: Record<string, unknown> | null) {
  server.use(
    http.get(`${ISSUES}/workflow/status`, () => HttpResponse.json({ success: true, data: { workflow: timeline } })),
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
  server.use(http.get(`${ISSUES}/workflow/artifacts`, () => HttpResponse.json({ success: true, data: artifacts })))
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
    http.get(
      `${ISSUES}/workflow/artifacts/${artifactId}/content`,
      () => new HttpResponse(content, { headers: { 'content-type': contentType } }),
    ),
  )
}

export function mockAgentStatus(status: Record<string, unknown>) {
  server.use(http.get(AGENT_STATUS, () => HttpResponse.json({ success: true, data: status })))
}

export function mockWorkflowRunSessions(sessions: Array<Record<string, unknown>>) {
  server.use(
    http.get('*/api/workflow-runs/:runId/sessions', () => HttpResponse.json({ success: true, data: sessions })),
  )
}

export function mockUpdateIssue(handler: (info: { request: Request }) => Promise<Response> | Response) {
  server.use(http.patch(ISSUES, handler as any))
}
