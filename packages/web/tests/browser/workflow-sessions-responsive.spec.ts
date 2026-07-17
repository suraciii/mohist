import { expect, test, type Locator, type Page } from '@playwright/test'

const project = {
  id: 'proj-workflow-sessions-responsive-browser',
  name: 'workflow-sessions-responsive-browser-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const issueNumber = 244
const workflowRunId = 'wr-responsive'
const longSessionName = 'review-repair-session-with-a-very-long-custom-name-that-must-truncate-inside-the-real-row'
const longFailureReason = 'probe timed out because the runner exceeded its budget and returned a long diagnostic string'
const transcriptSessionName = 'long-markdown-code-session'
const longMarkdownCodeLine = `const longCodeLine = '${'unbroken-code-token-'.repeat(24)}';`

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeIssue() {
  return {
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
    runtimeSessionId: 'acp-responsive',
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

function makeTranscriptSession() {
  return {
    ...makeSession(),
    id: 'session-responsive-code',
    sessionName: transcriptSessionName,
    runtimeSessionId: 'acp-responsive-code',
    status: 'completed',
    failureReason: '',
    exitCode: 0,
    eventSummary: {
      resolvedModel: 'configured/provider-name-with-long-model',
      failureCategory: 'none',
      toolCallCount: 0,
      toolErrorCount: 0,
    },
  }
}

function makeTranscriptMetadata() {
  return {
    id: 'session-responsive-code',
    sessionName: transcriptSessionName,
    runtimeSessionId: 'acp-responsive-code',
    status: 'completed',
    statusKind: 'completed',
    model: 'configured/provider-name-with-long-model',
    stage: 'check',
    title: 'Responsive transcript code block',
    createdAt: '2026-06-12T10:00:00.000Z',
    completedAt: '2026-06-12T10:05:00.000Z',
    lastActivityAt: '2026-06-12T10:05:00.000Z',
    lastDataAt: '2026-06-12T10:05:00.000Z',
    metadata: {
      partCount: 1,
      eventCount: 1,
      toolCount: 0,
      promptCount: 1,
    },
  }
}

function makeTranscriptResponse() {
  const at = '2026-06-12T10:05:00.000Z'
  return {
    turns: [
      {
        id: 'turn-responsive-code',
        startedAt: at,
        completedAt: at,
        user: {
          role: 'mohist',
          text: 'Render a long code line without widening the page.',
          kind: 'task',
          sentAt: at,
        },
        assistant: [
          {
            id: 'part-responsive-code',
            type: 'text',
            text: `The code block scrolls locally.\n\n\`\`\`ts\n${longMarkdownCodeLine}\n\`\`\``,
            startedAt: at,
            completedAt: at,
          },
        ],
      },
    ],
    partCount: 1,
    lastActivityAt: at,
  }
}

async function mockWorkflowSessionsApi(page: Page, sessions = [makeSession()]) {
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
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/coder-sessions`) {
      return route.fulfill({ json: apiResponse([]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/sessions/${transcriptSessionName}`) {
      return route.fulfill({ json: apiResponse(makeTranscriptMetadata()) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/sessions/${transcriptSessionName}/transcript`) {
      return route.fulfill({ json: apiResponse(makeTranscriptResponse()) })
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
      return route.fulfill({ json: apiResponse(sessions) })
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

async function expectNoDocumentHorizontalOverflow(page: Page) {
  const metrics = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }))
  expect(metrics.scrollWidth).toBeLessThanOrEqual(metrics.clientWidth)
}

async function expectBoxInsideViewport(page: Page, locator: Locator, label: string) {
  await expect(locator, label).toBeVisible()
  await locator.scrollIntoViewIfNeeded()

  const [box, viewport] = await Promise.all([
    locator.boundingBox(),
    page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight })),
  ])

  expect(box, `${label} has a rendered box`).not.toBeNull()
  expect(box!.x, `${label} starts inside the viewport`).toBeGreaterThanOrEqual(0)
  expect(box!.y, `${label} starts inside the viewport`).toBeGreaterThanOrEqual(0)
  expect(box!.x + box!.width, `${label} ends inside the viewport`).toBeLessThanOrEqual(viewport.width)
  expect(box!.y + box!.height, `${label} ends inside the viewport`).toBeLessThanOrEqual(viewport.height)
}

test('workflow session rows do not overflow a narrow panel in the real app', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 760 })
  await mockWorkflowSessionsApi(page)

  await page.goto(`/${project.name}/issues/${issueNumber}`)

  const panel = page.getByTestId('workflow-sessions-panel')
  const row = page.getByTestId('workflow-session-row').filter({ hasText: longSessionName })
  const metrics = row.getByTestId('workflow-session-row-metrics')

  await expect(panel).toBeVisible()
  await expect(row).toBeVisible()
  await expect(row.getByText(longSessionName)).toBeVisible()
  await expect(row.getByText('Failed')).toBeVisible()
  await expect(row.getByText(/27 tools.*3 errors/)).toBeVisible()
  await expect(row.getByText(longFailureReason)).toBeVisible()

  await expectNoElementHorizontalOverflow(panel)
  await expectNoElementHorizontalOverflow(row)
  await expectNoElementHorizontalOverflow(metrics)

  const headerLineCount = await row.getByTestId('workflow-session-row-header').evaluate((node) => {
    const children = Array.from(node.children)
    return new Set(children.map((child) => Math.round(child.getBoundingClientRect().top))).size
  })
  expect(headerLineCount).toBeGreaterThan(1)
})

test.describe('Workflow session transcript mobile overflow', () => {
  for (const width of [320, 390, 430]) {
    test(`keeps a long Markdown code line inside a ${width}px viewport`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 })
      await mockWorkflowSessionsApi(page, [makeTranscriptSession(), makeSession()])

      await page.goto(`/${project.name}/issues/${issueNumber}`)
      const transcriptRow = page.getByTestId('workflow-session-row').filter({ hasText: transcriptSessionName })
      await expect(transcriptRow).toBeVisible()
      await transcriptRow.click()
      await expect(page).toHaveURL(new RegExp(`/issues/${issueNumber}/workflow/sessions/${transcriptSessionName}$`))

      const codeBlock = page.locator('.transcript-md pre')
      const horizontalScrollOwner = codeBlock.locator(
        'xpath=ancestor-or-self::*[contains(concat(" ", normalize-space(@class), " "), " overflow-x-auto ")][1]',
      )

      await expect(codeBlock).toContainText(longMarkdownCodeLine)
      await expectNoDocumentHorizontalOverflow(page)
      await expectBoxInsideViewport(page, codeBlock, 'Transcript code block')
      await expectBoxInsideViewport(page, horizontalScrollOwner, 'Transcript horizontal scroll owner')
      await expectBoxInsideViewport(page, page.getByTestId('session-sibling-navigation-slot'), 'Sibling session navigation')

      const overflowX = await horizontalScrollOwner.evaluate((node) => getComputedStyle(node).overflowX)
      expect(['auto', 'scroll']).toContain(overflowX)
    })
  }
})
