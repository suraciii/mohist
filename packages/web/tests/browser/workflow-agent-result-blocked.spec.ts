import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-agent-result-blocked',
  name: 'agent-result-blocked',
  repositories: [],
  createdAt: '2026-08-14T10:50:00Z',
  updatedAt: '2026-08-14T11:10:00Z',
}

const issueNumber = 589
const workflowRunId = 'wr-agent-result-blocked'
type Phase = 'unknown' | 'blocked' | 'completed' | 'stopped'

function response(data: unknown) {
  return { success: true, data }
}

function issueFor(phase: Phase) {
  const blocked = phase === 'blocked'
  const completed = phase === 'completed'
  return {
    number: issueNumber,
    title: 'Preserve an unconfirmed Agent result',
    body: '',
    status: completed ? 'done' : 'in_progress',
    workflowStage: 'build',
    workflowStatus: completed ? 'completed' : phase === 'stopped' ? 'stopped' : blocked ? 'blocked' : 'running',
    workflowRunId,
    workflowProfileId: 'mohist/github-pr',
    health: completed ? 'done' : blocked ? 'blocked' : 'active',
    blockedReason: blocked ? 'Agent result unconfirmed' : null,
    projectId: project.id,
    projectName: project.name,
    labels: {},
    priority: 'p0',
    risk: null,
    createdAt: project.createdAt,
    updatedAt: project.updatedAt,
    comments: [],
    attachments: [],
    isDraft: false,
    canStart: false,
    blocker: null,
    prerequisites: [],
    feedback: [],
  }
}

function timelineFor(phase: Phase) {
  const blocked = phase === 'blocked'
  const terminal = phase === 'completed' || phase === 'stopped'
  const settlement =
    phase === 'unknown' || blocked
      ? {
          state: blocked ? 'blocked' : 'unknown',
          reason: blocked ? 'agent-result-unconfirmed' : 'runner-disconnected',
          message: 'Runner disconnected before the Agent result was accepted.',
          firstUnknownAt: '2026-08-14T10:56:58Z',
          deadlineAt: '2026-08-14T11:01:58Z',
          taskRunId: 'build.1',
          workId: 'build.1',
          runnerId: 'runner-pluto',
          agentSessionId: 'session-1',
          agentTurnId: 'turn-1',
          runtime: 'opencode',
          nextAction: 'Restore the original Runner and allow the result to replay.',
          recoveryActions: ['stop'],
        }
      : null
  const attention = blocked ? { ...settlement, state: 'blocked', reason: 'agent-result-unconfirmed' } : null
  return {
    workflowRunId,
    status: phase === 'completed' ? 'completed' : phase === 'stopped' ? 'stopped' : blocked ? 'blocked' : 'running',
    currentStage: 'build',
    pendingWork: null,
    stages: [
      {
        stage: 'build',
        status: blocked ? 'blocked' : terminal ? (phase === 'completed' ? 'completed' : 'running') : 'running',
        order: 0,
        startedAt: '2026-08-14T10:55:00Z',
        completedAt: phase === 'completed' ? '2026-08-14T11:02:00Z' : null,
        durationMs: null,
        tasks: [
          {
            id: 'build.1',
            title: 'Build blocked settlement projection',
            uses: 'mohist/opencode',
            sessionName: 'session-1',
            status:
              phase === 'completed' ? 'completed' : phase === 'stopped' ? 'cancelled' : blocked ? 'blocked' : 'running',
            startedAt: '2026-08-14T10:55:00Z',
            completedAt: phase === 'completed' ? '2026-08-14T11:02:00Z' : null,
            durationMs: null,
            attempts: 1,
            message: settlement?.message ?? null,
            agentResultSettlement: settlement,
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: blocked ? [{ name: 'stop', label: 'Stop workflow', target: null }] : [],
    agentResultAttention: attention,
  }
}

async function mockApi(page: Page, getPhase: () => Phase) {
  await page.route('**/hubs/events**', (route) => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()
    const phase = getPhase()

    if (method === 'GET' && path === '/auth/session') return route.fulfill({ json: response(null) })
    if (method === 'GET' && path === '/projects') return route.fulfill({ json: response([project]) })
    if (method === 'GET' && path === `/projects/${project.id}/issues`)
      return route.fulfill({ json: response([issueFor(phase)]) })
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}`)
      return route.fulfill({ json: response(issueFor(phase)) })
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/workflow/status`)
      return route.fulfill({ json: response({ workflow: timelineFor(phase) }) })
    if (method === 'GET' && path === `/projects/${project.id}/agent/status`)
      return route.fulfill({
        json: response({ running: false, runnerAvailable: true, activeAgents: [], capacity: { active: 0, max: 4 } }),
      })
    if (method === 'GET' && path.includes('/workflow/tasks/') && path.endsWith('/logs'))
      return route.fulfill({ json: response({ lines: [], nextCursor: null, truncated: false }) })
    if (method === 'GET' && path === `/workflow-runs/${workflowRunId}/sessions`)
      return route.fulfill({ json: response([]) })
    if (method === 'GET' && path.endsWith('/workflow/artifacts')) return route.fulfill({ json: response([]) })
    if (method === 'GET' && path.endsWith('/events')) return route.fulfill({ json: response([]) })
    if (method === 'GET' && (path.endsWith('/diff') || path.endsWith('/commits')))
      return route.fulfill({ json: response({ available: false, message: 'No fixture' }) })
    if (method === 'GET' && path.endsWith('/workspace-status'))
      return route.fulfill({ json: response({ exists: false, reason: 'not_started' }) })
    if (method === 'GET' && (path.endsWith('/variables') || path.endsWith('/workflow-profile')))
      return route.fulfill({ json: response({ vars: {}, stages: {} }) })
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profile/default`)
      return route.fulfill({
        json: response({
          projectId: project.id,
          defaultWorkflowProfileId: 'mohist/github-pr',
          disabledWorkflowProfileIds: [],
        }),
      })
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profiles`)
      return route.fulfill({ json: response([]) })
    if (method === 'GET' && path.endsWith('/opencode/models'))
      return route.fulfill({ json: response({ models: [], modelVariants: {} }) })

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled fixture: ${method} ${path}` } })
  })
}

test('unknown Agent result becomes blocked and a late result clears blocked presentation', async ({ page }) => {
  let phase: Phase = 'unknown'
  await mockApi(page, () => phase)
  await page.goto(`/${project.name}/issues/${issueNumber}`)

  await expect(page.getByTestId('workflow-stage-bar')).toBeVisible()
  await expect(page.getByTestId('workflow-agent-result-attention')).toHaveCount(0)
  await expect(page.getByTestId('workflow-task-item')).toContainText('Build blocked settlement projection')
  await expect(page.getByTestId('workflow-task-item').getByText('failed', { exact: true })).toHaveCount(0)

  phase = 'blocked'
  await page.reload()

  const attention = page.getByTestId('workflow-agent-result-attention')
  await expect(attention).toBeVisible()
  await expect(attention).toContainText('Agent result unconfirmed')
  await expect(attention).toContainText('session-1')
  await expect(attention).toContainText('turn-1')
  const task = page.getByTestId('workflow-task-item')
  await expect(task.getByText('blocked', { exact: true })).toBeVisible()
  await task.getByRole('button').click()
  await expect(task.getByTestId('workflow-task-blocked-attention')).toBeVisible()
  await expect(task.getByText('failed', { exact: true })).toHaveCount(0)

  phase = 'completed'
  await page.reload()

  await expect(page.getByTestId('workflow-agent-result-attention')).toHaveCount(0)
  await page
    .getByTestId('workflow-stage-bar')
    .getByRole('button', { name: /^Build/ })
    .click()
  const completedTask = page.getByTestId('workflow-task-item')
  await expect(completedTask).toContainText('Build blocked settlement projection')
  await expect(completedTask.getByText('blocked', { exact: true })).toHaveCount(0)
  await expect(completedTask.getByText('failed', { exact: true })).toHaveCount(0)
})

test('explicit stop clears unresolved attention without a failed presentation', async ({ page }) => {
  let phase: Phase = 'unknown'
  await mockApi(page, () => phase)
  await page.goto(`/${project.name}/issues/${issueNumber}`)
  await expect(page.getByTestId('workflow-task-item')).toBeVisible()

  phase = 'stopped'
  await page.reload()

  await expect(page.getByTestId('workflow-agent-result-attention')).toHaveCount(0)
  const task = page.getByTestId('workflow-task-item')
  await expect(task).toContainText('Build blocked settlement projection')
  await expect(task.getByText('failed', { exact: true })).toHaveCount(0)
})
