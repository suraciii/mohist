import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-workflow-sessions-responsive-e2e',
  name: 'workflow-sessions-responsive-e2e-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const issueNumber = 244
const workflowRunId = 'wr-responsive'
const longSessionName = 'review-repair-session-with-a-very-long-custom-name-that-must-truncate-inside-the-real-row'
const longFailureReason = 'probe timed out because the runner exceeded its budget and returned a long diagnostic string'

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeIssue() {
  return {
    id: 'issue-responsive',
    number: issueNumber,
    title: 'Session row responsive layout',
    body: 'Verify the real workflow sessions panel at narrow widths.',
    status: 'in_progress',
    workflowStage: 'check',
    workflowStatus: 'running',
    workflowRunId,
    workflowProfileId: 'mohist/github-pr',
    health: 'active',
    projectId: project.id,
    labels: {},
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
    comments: [],
    attachments: [],
    priority: 'p2',
    model: null,
    modelVariant: null,
    agentConfig: null,
    stageModels: {},
    stageModelVariants: {},
    prerequisites: [],
    isDraft: false,
    canStart: false,
    blocker: null,
    feedback: [],
  }
}

function makeSession() {
  return {
    id: 'session-responsive',
    workflowRunId,
    sessionName: longSessionName,
    acpSessionId: 'acp-responsive',
    projectId: project.id,
    issueNumber,
    runnerId: 'runner-responsive',
    status: 'failed',
    stage: 'check',
    model: 'configured/provider-name-with-long-model',
    workDir: null,
    processPid: null,
    createdAt: '2026-06-12T10:00:00.000Z',
    startedAt: '2026-06-12T10:00:02.000Z',
    completedAt: '2026-06-12T10:05:00.000Z',
    lastDataAt: '2026-06-12T10:05:00.000Z',
    failureReason: longFailureReason,
    exitCode: 1,
    usage: {
      totalTokens: 588_371,
      costAmount: 12.34,
      costCurrency: 'USD',
      contextWindowUsed: 476_000,
      contextWindowSize: 512_000,
    },
    eventSummary: {
      resolvedModel: 'resolved/provider-name-with-even-longer-model',
      failureCategory: 'timeout',
      toolCallCount: 27,
      toolErrorCount: 3,
    },
  }
}

async function mockWorkflowSessionsApi(page: Page) {
  await page.route('**/hubs/events**', route => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()

    if (method === 'GET' && path === '/projects') {
      return route.fulfill({ json: apiResponse([project]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/agent/status`) {
      return route.fulfill({ json: apiResponse({ running: false, runnerAvailable: true, activeAgents: [], capacity: { active: 0, max: 8 } }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}`) {
      return route.fulfill({ json: apiResponse(makeIssue()) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/diff`) {
      return route.fulfill({ json: apiResponse({ available: false, message: 'No diff in responsive test' }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/commits`) {
      return route.fulfill({ json: apiResponse({ available: false, message: 'No commits in responsive test' }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/workflow/status`) {
      return route.fulfill({ json: apiResponse({ workflow: null }) })
    }
    if (method === 'GET' && path === `/workflow-runs/${workflowRunId}/sessions`) {
      return route.fulfill({ json: apiResponse([makeSession()]) })
    }

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
  })
}

async function expectNoElementHorizontalOverflow(locator: ReturnType<Page['getByTestId']>) {
  const metrics = await locator.evaluate((node) => ({
    scrollWidth: node.scrollWidth,
    clientWidth: node.clientWidth,
  }))
  expect(metrics.scrollWidth).toBeLessThanOrEqual(metrics.clientWidth)
}

test('workflow session rows do not overflow a narrow panel in the real app', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 760 })
  await mockWorkflowSessionsApi(page)

  await page.goto(`/${project.name}/issues/${issueNumber}`)

  const panel = page.getByTestId('workflow-sessions-panel')
  const row = page.getByTestId('workflow-session-row')
  const metrics = page.getByTestId('workflow-session-row-metrics')

  await expect(panel).toBeVisible()
  await expect(row).toBeVisible()
  await expect(row.getByText(longSessionName)).toBeVisible()
  await expect(row.getByText('Failed')).toBeVisible()
  await expect(row.getByText(/27 tools.*3 errors/)).toBeVisible()
  await expect(row.getByText(longFailureReason)).toBeVisible()

  await expectNoElementHorizontalOverflow(panel)
  await expectNoElementHorizontalOverflow(row)
  await expectNoElementHorizontalOverflow(metrics)

  const headerLineCount = await page.getByTestId('workflow-session-row-header').evaluate((node) => {
    const children = Array.from(node.children)
    return new Set(children.map((child) => Math.round(child.getBoundingClientRect().top))).size
  })
  expect(headerLineCount).toBeGreaterThan(1)
})
