import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-activity-browser',
  name: 'activity-browser-project',
  repositories: [],
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
}

function response(data: unknown) {
  return { success: true, data }
}

const events = [
  {
    id: 1,
    origin: 'workflowrun',
    sourceAggregateKind: 'workflow-run',
    sourceAggregateId: 'wr-activity',
    source: '/mohist/workflow-runs/wr-activity',
    type: 'com.mohist.workflow.stage.failed',
    time: '2026-01-01T02:00:00.000Z',
    envelopeId: 'failure-1',
    specVersion: '1.0',
    subject: '42',
    dataContentType: 'application/json',
    data: { stage: 'Check' },
    extensions: { issue: '42' },
    runnerId: null,
    issueNumber: 42,
  },
  {
    id: 2,
    origin: 'agentsession',
    sourceAggregateKind: 'agent-session',
    sourceAggregateId: 'session-42',
    source: '/mohist/agent-session/session-42',
    type: 'coder_session_started',
    time: '2026-01-01T01:00:00.000Z',
    envelopeId: 'session-1',
    specVersion: '1.0',
    subject: 'session-42',
    dataContentType: 'application/json',
    data: { issueNumber: 42 },
    extensions: { issue: '42' },
    runnerId: null,
    issueNumber: 42,
    sessionSourceKind: 'workflow',
  },
  {
    id: 3,
    origin: 'agentsession',
    sourceAggregateKind: 'agent-session',
    sourceAggregateId: 'session-42',
    source: '/mohist/agent-session/session-42',
    type: 'com.mohist.runner.disconnected',
    time: '2026-01-01T00:00:00.000Z',
    envelopeId: 'runner-1',
    specVersion: '1.0',
    subject: null,
    dataContentType: 'application/json',
    data: { runnerId: 'runner-42' },
    extensions: {},
    runnerId: 'runner-42',
  },
]

async function mockActivityApi(page: Page) {
  await page.route('**/hubs/events**', (route) => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')

    if (route.request().method() === 'GET' && path === '/projects') return route.fulfill({ json: response([project]) })
    if (route.request().method() === 'GET' && path === `/projects/${project.id}/agent/status`) {
      return route.fulfill({ json: response({ running: false, runnerAvailable: true, activeAgents: [], capacity: { active: 0, max: 8 } }) })
    }
    if (route.request().method() === 'GET' && path === `/projects/${project.id}/events`) return route.fulfill({ json: response(events) })
    if (route.request().method() === 'GET' && path === `/projects/${project.id}/agent/activity`) {
      return route.fulfill({ json: response({ summary: { active: 0, waiting: 0, completed: 0, failed: 0, slots: { active: 0, max: 8 } }, sessions: [], waiting: [] }) })
    }
    if (route.request().method() === 'GET' && path === `/projects/${project.id}/runners`) {
      return route.fulfill({ json: response({ runners: [{ id: 'runner-42', kind: 'external', hostname: 'runner', scope: { type: 'global' }, status: 'idle', capabilities: [], coderModels: [], coderModelCount: 0, activeWorks: [] }] }) })
    }
    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled route: ${path}` } })
  })
}

async function expectNoHorizontalOverflow(page: Page) {
  const metrics = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }))
  expect(metrics.scrollWidth).toBeLessThanOrEqual(metrics.clientWidth)
}

test('Activity renders evidence zones and project-scoped navigation at desktop width', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await mockActivityApi(page)
  await page.goto(`/${project.name}/activity`)

  await expect(page.getByTestId('activity-attention-zone')).toBeVisible()
  await expect(page.getByTestId('activity-routine-zone')).toBeVisible()
  await expect(page.getByTestId('activity-event-entry')).toHaveCount(3)
  await expect(page.getByTestId('activity-event-primary-link').first()).toHaveAttribute('href', `/${project.name}/issues/42?from=activity`)
  await expect(page.getByTestId('activity-event-session-link')).toHaveAttribute('href', `/${project.name}/issues/42/session/session-42?from=activity`)
  await expect(page.getByTestId('activity-event-runner-link')).toHaveAttribute('href', `/${project.name}/runners/runner-42?from=activity`)

  await page.getByTestId('activity-event-primary-link').first().click()
  await expect(page).toHaveURL(`/${project.name}/issues/42?from=activity`)
  await page.goBack()

  await page.getByTestId('activity-event-session-link').click()
  await expect(page).toHaveURL(`/${project.name}/issues/42/session/session-42?from=activity`)
  await page.goBack()

  await page.getByTestId('activity-event-runner-link').click()
  await expect(page).toHaveURL(`/${project.name}/runners/runner-42?from=activity`)
})

test('Activity filters wrap without horizontal overflow on a narrow viewport', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 760 })
  await mockActivityApi(page)
  await page.goto(`/${project.name}/activity`)

  const filterBar = page.getByTestId('activity-filter-bar')
  await expect(filterBar).toBeVisible()
  await expect(page.getByTestId('activity-attention-zone')).toBeVisible()
  await expect(page.getByTestId('activity-routine-zone')).toBeVisible()

  const filterLines = await filterBar.locator('button').evaluateAll((buttons) => new Set(buttons.map((button) => Math.round(button.getBoundingClientRect().top))).size)
  expect(filterLines).toBeGreaterThan(1)
  await expectNoHorizontalOverflow(page)
})
