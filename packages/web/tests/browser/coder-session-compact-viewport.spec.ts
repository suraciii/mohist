import { expect, test, type Locator, type Page } from '@playwright/test'
import type { WorkflowRunSession } from '../../src/entities/coder-session/model/types'

const project = {
  id: 'proj-coder-session-compact-viewport',
  name: 'coder-session-compact-viewport-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const issueNumber = 411
const workflowRunId = 'wr-coder-session-compact-viewport'
const sessionName = 'integrate'

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeIssue() {
  return {
    id: 'issue-compact-viewport',
    number: issueNumber,
    title: 'Compact viewport integration issue',
    body: 'Mock issue for compact-viewport pixel verification.',
    status: 'in_progress',
    workflowStage: 'integrate',
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
    priority: 'p1',
    risk: 'medium',
    model: null,
    modelVariant: null,
    agentConfig: null,
    stageModels: {},
    stageModelVariants: {},
    prerequisites: [],
    prerequisiteNumbers: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    feedback: [],
  }
}

function makeCompactViewportSession(): WorkflowRunSession {
  return {
    id: `session-${sessionName}`,
    workflowRunId,
    sessionName,
    acpSessionId: `acp-${sessionName}`,
    projectId: project.id,
    issueNumber,
    runnerId: 'runner-compact-viewport',
    status: 'failed',
    stage: 'integrate',
    model: 'minimax/MiniMax-M3',
    workDir: null,
    processPid: null,
    createdAt: '2026-06-12T10:00:00.000Z',
    startedAt: '2026-06-12T10:00:02.000Z',
    completedAt: '2026-06-12T10:05:00.000Z',
    lastDataAt: '2026-06-12T10:05:00.000Z',
    failureReason: 'context window exceeded',
    exitCode: 1,
    eventSummary: {
      resolvedModel: 'minimax/MiniMax-M3',
      failureCategory: 'context_limit',
      toolCallCount: 12,
      toolErrorCount: 1,
    },
    usage: {
      totalTokens: 31_200,
      inputTokens: 28_000,
      outputTokens: 3_200,
      costAmount: 0.42,
      costCurrency: 'USD',
      contextWindowUsed: 30_000,
      contextWindowSize: 32_000,
      contextUsagePercent: 94,
      healthStatus: 'red',
    },
  }
}

function makeCompactViewportMetadata() {
  return {
    id: `session-${sessionName}`,
    sessionName,
    acpSessionId: `acp-${sessionName}`,
    status: 'failed',
    statusKind: 'failed',
    model: 'minimax/MiniMax-M3',
    stage: 'integrate',
    title: 'Integrate session',
    createdAt: '2026-06-12T10:00:00.000Z',
    completedAt: '2026-06-12T10:05:00.000Z',
    lastActivityAt: '2026-06-12T10:05:00.000Z',
    lastDataAt: '2026-06-12T10:05:00.000Z',
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: 'context window exceeded',
    turnCount: 3,
    changedFiles: [
      { path: 'src/index.ts', operation: 'modified', additions: 12, deletions: 3 },
    ],
    metadata: { eventCount: 9, toolCount: 4, partCount: 6 },
    usage: {
      totalTokens: 31_200,
      inputTokens: 28_000,
      outputTokens: 3_200,
      cachedReadTokens: 1_200,
      thoughtTokens: 600,
      costAmount: 0.42,
      costCurrency: 'USD',
      contextWindowUsed: 30_000,
      contextWindowSize: 32_000,
      contextUsagePercent: 94,
      healthStatus: 'red',
    },
    eventSummary: {
      resolvedModel: 'minimax/MiniMax-M3',
      failureCategory: 'context_limit',
      toolCallCount: 12,
      toolErrorCount: 1,
    },
  }
}

function makeCompactViewportTranscript() {
  const at = '2026-06-12T10:05:00.000Z'
  return {
    turns: [
      {
        id: 'turn-1',
        startedAt: at,
        completedAt: at,
        user: {
          role: 'mohist',
          text: 'Compact viewport turn 1 user prompt',
          kind: 'task',
          sentAt: at,
        },
        assistant: [
          {
            id: 'part-1',
            type: 'text',
            text: 'Compact viewport assistant response 1.',
            startedAt: at,
            completedAt: at,
          },
        ],
      },
      {
        id: 'turn-2',
        startedAt: at,
        completedAt: at,
        user: {
          role: 'mohist',
          text: 'Compact viewport turn 2 user prompt',
          kind: 'task',
          sentAt: at,
        },
        assistant: [
          {
            id: 'part-2',
            type: 'text',
            text: 'Compact viewport assistant response 2.',
            startedAt: at,
            completedAt: at,
          },
        ],
      },
      {
        id: 'turn-3',
        startedAt: at,
        completedAt: at,
        user: {
          role: 'mohist',
          text: 'Compact viewport turn 3 user prompt',
          kind: 'task',
          sentAt: at,
        },
        assistant: [
          {
            id: 'part-3',
            type: 'text',
            text: 'Compact viewport assistant response 3.',
            startedAt: at,
            completedAt: at,
          },
        ],
      },
    ],
    partCount: 3,
    lastActivityAt: at,
  }
}

async function mockCoderSessionApi(page: Page, session = makeCompactViewportSession()) {
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
    if (method === 'GET' && path === `/projects/${project.id}/issues`) {
      return route.fulfill({ json: apiResponse([makeIssue()]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}`) {
      return route.fulfill({ json: apiResponse(makeIssue()) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/coder-sessions`) {
      return route.fulfill({ json: apiResponse([session]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/sessions/${sessionName}`) {
      return route.fulfill({ json: apiResponse(makeCompactViewportMetadata()) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/sessions/${sessionName}/transcript`) {
      return route.fulfill({ json: apiResponse(makeCompactViewportTranscript()) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/diff`) {
      return route.fulfill({ json: apiResponse({ available: false, message: 'no diff' }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/commits`) {
      return route.fulfill({ json: apiResponse({ available: false, message: 'no commits' }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/workflow/status`) {
      return route.fulfill({ json: apiResponse({ workflow: null }) })
    }
    if (method === 'GET' && path === `/workflow-runs/${workflowRunId}/sessions`) {
      return route.fulfill({ json: apiResponse([session]) })
    }

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
  })
}

async function transcriptHeight(page: Page): Promise<number> {
  return page.evaluate(() => {
    const node = document.querySelector('[data-testid="session-transcript-scroll-container"]') as HTMLElement | null
    if (!node) return 0
    const rect = node.getBoundingClientRect()
    return rect.height
  })
}

async function boxOf(_page: Page, locator: Locator) {
  await expect(locator, 'locator should be visible').toBeVisible()
  const value = await locator.boundingBox()
  expect(value, 'locator should have a bounding box').not.toBeNull()
  return value!
}

async function navBox(page: Page) {
  const nav = page.locator('nav.fixed.bottom-0')
  await expect(nav, 'mobile bottom nav should be visible').toBeVisible()
  return boxOf(page, nav)
}

test.describe('Coder Session compact viewport — pixel verification', () => {
  test('preserves transcript height > 100px at 375x667', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 667 })
    await mockCoderSessionApi(page)

    await page.goto(`/${project.name}/issues/${issueNumber}/workflow/sessions/${sessionName}`)

    const transcriptScrollContainer = page.getByTestId('session-transcript-scroll-container')
    await expect(transcriptScrollContainer).toBeVisible()

    await page.waitForTimeout(150)
    const height = await transcriptHeight(page)
    expect(height, 'transcript must have non-zero visible height at 375x667').toBeGreaterThan(100)
  })

  test('preserves transcript height > 0px at 320x568', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 568 })
    await mockCoderSessionApi(page)

    await page.goto(`/${project.name}/issues/${issueNumber}/workflow/sessions/${sessionName}`)

    const transcriptScrollContainer = page.getByTestId('session-transcript-scroll-container')
    await expect(transcriptScrollContainer).toBeVisible()

    await page.waitForTimeout(150)
    const height = await transcriptHeight(page)
    expect(height, 'transcript must never collapse to zero at 320x568').toBeGreaterThan(0)
  })

  test('Compact and Reset recovery controls are not covered by mobile bottom nav at 375x667', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 667 })
    await mockCoderSessionApi(page)

    await page.goto(`/${project.name}/issues/${issueNumber}/workflow/sessions/${sessionName}`)

    const compact = page.getByTestId('session-recovery-compact')
    const reset = page.getByTestId('session-recovery-reset')
    await expect(compact).toBeVisible()
    await expect(reset).toBeVisible()

    const nav = await navBox(page)
    const compactBox = await boxOf(page, compact)
    const resetBox = await boxOf(page, reset)

    const viewportHeight = await page.evaluate(() => window.innerHeight)
    expect(viewportHeight).toBe(667)

    expect(compactBox.y, 'compact top within viewport').toBeGreaterThanOrEqual(0)
    expect(compactBox.y + compactBox.height, 'compact bottom within viewport').toBeLessThanOrEqual(viewportHeight)
    expect(resetBox.y, 'reset top within viewport').toBeGreaterThanOrEqual(0)
    expect(resetBox.y + resetBox.height, 'reset bottom within viewport').toBeLessThanOrEqual(viewportHeight)

    expect(compactBox.y + compactBox.height, 'compact above nav').toBeLessThanOrEqual(nav.y)
    expect(resetBox.y + resetBox.height, 'reset above nav').toBeLessThanOrEqual(nav.y)
  })

  test('Compact and Reset recovery controls are reachable at 320x568 (never covered by nav)', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 568 })
    await mockCoderSessionApi(page)

    await page.goto(`/${project.name}/issues/${issueNumber}/workflow/sessions/${sessionName}`)

    const compact = page.getByTestId('session-recovery-compact')
    const reset = page.getByTestId('session-recovery-reset')
    await expect(compact).toBeVisible()
    await expect(reset).toBeVisible()

    const nav = await navBox(page)
    const compactBox = await boxOf(page, compact)
    const resetBox = await boxOf(page, reset)

    const viewportHeight = await page.evaluate(() => window.innerHeight)
    expect(viewportHeight).toBe(568)

    expect(compactBox.y, 'compact top within viewport at 320x568').toBeGreaterThanOrEqual(0)
    expect(compactBox.y + compactBox.height, 'compact bottom within viewport at 320x568').toBeLessThanOrEqual(viewportHeight)
    expect(resetBox.y, 'reset top within viewport at 320x568').toBeGreaterThanOrEqual(0)
    expect(resetBox.y + resetBox.height, 'reset bottom within viewport at 320x568').toBeLessThanOrEqual(viewportHeight)

    expect(compactBox.y + compactBox.height, 'compact above nav at 320x568').toBeLessThanOrEqual(nav.y)
    expect(resetBox.y + resetBox.height, 'reset above nav at 320x568').toBeLessThanOrEqual(nav.y)
  })

  test('follow-up composer is reachable and not covered by mobile navigation at 375x667', async ({ page }) => {
    const baseSession = makeCompactViewportSession()
    const runningSession: typeof baseSession = {
      ...baseSession,
      status: 'active',
      completedAt: null,
      lastDataAt: '2026-06-12T10:05:30.000Z',
    }
    await page.setViewportSize({ width: 375, height: 667 })
    await mockCoderSessionApi(page, runningSession)

    await page.goto(`/${project.name}/issues/${issueNumber}/workflow/sessions/${sessionName}`)

    const composer = page.getByTestId('session-followup-composer')
    await expect(composer).toBeVisible()

    const composerBox = await boxOf(page, composer)
    const nav = await navBox(page)

    expect(composerBox.y + composerBox.height, 'composer bottom above nav (allow 1px sub-pixel rounding)').toBeLessThanOrEqual(nav.y + 1)
  })

  test('follow-up composer is reachable and not covered by mobile navigation at 320x568', async ({ page }) => {
    const baseSession = makeCompactViewportSession()
    const runningSession: typeof baseSession = {
      ...baseSession,
      status: 'active',
      completedAt: null,
      lastDataAt: '2026-06-12T10:05:30.000Z',
    }
    await page.setViewportSize({ width: 320, height: 568 })
    await mockCoderSessionApi(page, runningSession)

    await page.goto(`/${project.name}/issues/${issueNumber}/workflow/sessions/${sessionName}`)

    const composer = page.getByTestId('session-followup-composer')
    await expect(composer).toBeVisible()

    const composerBox = await boxOf(page, composer)
    const nav = await navBox(page)

    expect(composerBox.y + composerBox.height, 'composer bottom above nav at 320x568 (allow 1px sub-pixel rounding)').toBeLessThanOrEqual(nav.y + 1)
  })

  test('mobile bottom navigation does not overlap the transcript content area at 375x667', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 667 })
    await mockCoderSessionApi(page)

    await page.goto(`/${project.name}/issues/${issueNumber}/workflow/sessions/${sessionName}`)

    const transcriptScrollContainer = page.getByTestId('session-transcript-scroll-container')
    await expect(transcriptScrollContainer).toBeVisible()

    await page.waitForTimeout(150)
    const transcriptBox = await transcriptScrollContainer.boundingBox()
    const nav = await navBox(page)
    expect(transcriptBox).not.toBeNull()

    expect(
      transcriptBox!.y + transcriptBox!.height,
      'transcript bottom must be above nav top',
    ).toBeLessThanOrEqual(nav.y)
  })

  test('mobile bottom navigation does not overlap the transcript content area at 320x568', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 568 })
    await mockCoderSessionApi(page)

    await page.goto(`/${project.name}/issues/${issueNumber}/workflow/sessions/${sessionName}`)

    const transcriptScrollContainer = page.getByTestId('session-transcript-scroll-container')
    await expect(transcriptScrollContainer).toBeVisible()

    await page.waitForTimeout(150)
    const transcriptBox = await transcriptScrollContainer.boundingBox()
    const nav = await navBox(page)
    expect(transcriptBox).not.toBeNull()

    expect(
      transcriptBox!.y + transcriptBox!.height,
      'transcript bottom must be above nav top at 320x568',
    ).toBeLessThanOrEqual(nav.y)
  })
})
