import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-agent-browser',
  name: 'agent-browser-project',
  repositories: [
    { name: 'web', gitUrl: 'https://github.com/example/web.git', baseBranch: 'main', isDefault: true },
  ],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const agents = [
  {
    id: 'agent-ready',
    projectId: project.id,
    name: 'Review Agent',
    avatar: 'review',
    description: 'Reviews pull requests',
    instructions: 'Review changes carefully',
    agentConfig: { runtime: 'opencode', model: 'openai/gpt-4.1' },
    skills: ['review'],
    allowedSubagentAgentIds: [],
    maxConcurrentRuns: 2,
    status: 'active',
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
    readiness: { conclusion: 'Ready', gaps: [], setup: null },
  },
  {
    id: 'agent-setup',
    projectId: project.id,
    name: 'Setup Agent',
    avatar: null,
    description: '',
    instructions: 'Needs setup',
    agentConfig: null,
    skills: [],
    allowedSubagentAgentIds: [],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
    readiness: { conclusion: 'Needs setup', gaps: [], setup: null },
  },
]

const workspaces = [{
  projectId: project.id,
  name: 'review-workspace',
  origin: { kind: 'manual' },
  repositories: ['web'],
  status: 'active',
  home: null,
  createdAt: '2026-06-01T00:00:00Z',
  boundSessionCount: 0,
}]

function apiResponse(data: unknown) {
  return { success: true, data }
}

async function mockAgentApi(page: Page, onLaunch?: (body: Record<string, unknown>) => void) {
  await page.route('**/hubs/events**', (route) => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()

    if (method === 'GET' && path === '/projects') return route.fulfill({ json: apiResponse([project]) })
    if (method === 'GET' && path === `/projects/${project.id}/agents`) return route.fulfill({ json: apiResponse(agents) })
    if (method === 'GET' && path === `/projects/${project.id}/agents/availability`) {
      return route.fulfill({ json: apiResponse([{ agentId: 'agent-ready', canStartNow: true, waitingReason: null, activeRuns: 0, maxConcurrentRuns: 2, capacity: { usedSlots: 0, totalSlots: 4 }, queuedCount: 0 }]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/repositories`) return route.fulfill({ json: apiResponse(project.repositories) })
    if (method === 'GET' && path === `/projects/${project.id}/workspaces`) return route.fulfill({ json: apiResponse(workspaces) })
    if (method === 'GET' && path === `/projects/${project.id}/issues`) return route.fulfill({ json: apiResponse([{ number: 42, title: 'Review task', projectId: project.id, status: 'backlog', health: 'active', labels: {}, createdAt: project.createdAt, updatedAt: project.updatedAt, isDraft: false, canStart: true, blocker: null }]) })
    if (method === 'GET' && path === `/projects/${project.id}/epics`) return route.fulfill({ json: apiResponse([{ number: 7, title: 'Quality', description: '', projectId: project.id, priority: 'p2', status: 'idle', createdAt: project.createdAt, updatedAt: project.updatedAt, progress: { deliveredCount: 0, totalIssueCount: 0, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false } }]) })
    if (method === 'POST' && path === `/projects/${project.id}/agents/agent-ready/sessions`) {
      onLaunch?.(route.request().postDataJSON() as Record<string, unknown>)
      return route.fulfill({ json: apiResponse({ sessionId: 'session-browser-1', jobId: 'job-browser-1', agentId: 'agent-ready', agentName: 'Review Agent', workspaceId: 'review-workspace', targetId: 'agent-ready', origin: 'web', status: 'queued', transcriptUrl: `/api/projects/${project.id}/agent-sessions/session-browser-1/transcript`, sessionUrl: `/${project.name}/sessions/session-browser-1` }) })
    }

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
  })
}

test.describe('task-oriented Agent UX', () => {
  test('lists purpose, readiness, and not-executable states distinctly', async ({ page }) => {
    await mockAgentApi(page)
    await page.goto(`/${project.name}/agents`)

    const readyRow = page.getByTestId('agent-row-agent-ready')
    await expect(readyRow).toBeVisible()
    await expect(readyRow.getByTestId('agent-purpose-agent-ready')).toHaveText('Reviews pull requests')
    await expect(readyRow.getByTestId('agent-readiness-agent-ready')).toHaveText('Readiness: Ready')
    await expect(readyRow.getByTestId('agent-executability-agent-ready')).toHaveText('Executable')

    const setupRow = page.getByTestId('agent-row-agent-setup')
    await expect(setupRow.getByTestId('agent-purpose-agent-setup')).toHaveText('No purpose set')
    await expect(setupRow.getByTestId('agent-readiness-agent-setup')).toHaveText('Readiness: Needs setup')
    await expect(setupRow.getByTestId('agent-executability-agent-setup')).toHaveText('Not executable')
  })

  test('reviews and submits repository/workspace/Issue/Epic scope', async ({ page }) => {
    let launchBody: Record<string, unknown> | undefined
    await mockAgentApi(page, (body) => { launchBody = body })
    await page.goto(`/${project.name}/agent-sessions/new?agent=agent-ready`)

    await expect(page.getByTestId('launch-scope-review')).toBeVisible()
    await page.getByTestId('launch-repository').selectOption('web')
    await page.getByTestId('launch-workspace').selectOption('review-workspace')
    await page.getByTestId('launch-issue').selectOption('42')
    await page.getByTestId('launch-epic').selectOption('7')
    await expect(page.getByTestId('scope-permissions')).toContainText('review-workspace')

    await page.getByPlaceholder('Enter your prompt for the agent...').fill('Review this task')
    await page.getByTestId('launch-button').click()
    await expect(page).toHaveURL(new RegExp(`/sessions/session-browser-1$`))

    expect(launchBody).toMatchObject({
      prompt: 'Review this task',
      context: { repository: 'web', workspace: 'review-workspace', issueNumber: 42, epicNumber: 7 },
    })
    expect(launchBody).not.toHaveProperty('runtime')
    expect(launchBody).not.toHaveProperty('model')
    expect(launchBody).not.toHaveProperty('instructions')
    expect(launchBody).not.toHaveProperty('maxConcurrentRuns')
  })
})
