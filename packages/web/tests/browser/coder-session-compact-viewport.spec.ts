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
const genericSessionId = 'generic-cancel-session'

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeIssue() {
  return {
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
    runtimeSessionId: `runtime-${sessionName}`,
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

function makeCompactViewportTranscript() {
  const at = '2026-06-12T10:05:00.000Z'
  const turns = Array.from({ length: 12 }, (_, index) => {
    const number = index + 1
    return {
      id: `turn-${number}`,
      startedAt: at,
      completedAt: at,
      user: {
        role: 'mohist',
        text: `Compact viewport turn ${number} user prompt with enough evidence to scroll the transcript.`,
        kind: 'task',
        sentAt: at,
      },
      assistant: [
        {
          id: `part-${number}`,
          type: 'text',
          text: `Compact viewport assistant response ${number} with enough evidence to scroll the transcript.`,
          startedAt: at,
          completedAt: at,
        },
      ],
    }
  })

  return {
    turns,
    partCount: turns.length,
    lastActivityAt: at,
  }
}

function makeGenericRunningSession() {
  return {
    sessionId: genericSessionId,
    agentId: 'agent-cancel',
    agentName: 'Cancellation Agent',
    runtimeSessionId: 'runtime-cancel-session',
    runtime: 'opencode',
    status: 'active',
    createdAt: '2026-06-12T10:00:00.000Z',
    lastActivityAt: '2026-06-12T10:05:00.000Z',
    resolvedModel: 'minimax/MiniMax-M3',
    failureCategory: null,
    toolCallCount: 12,
    toolErrorCount: 0,
    contextRefs: null,
    usage: {
      totalTokens: 15_000,
      inputTokens: 12_000,
      outputTokens: 3_000,
      costAmount: 0.21,
      costCurrency: 'USD',
      contextWindowUsed: 15_000,
      contextWindowSize: 32_000,
      contextUsagePercent: 47,
    },
  }
}

function makeUnifiedWorkflowSummary(session: WorkflowRunSession) {
  const isActive = session.status === 'active' || session.status === 'running' || session.status === 'probing'
  return {
    id: session.id,
    source: 'workflow',
    runtimeSessionId: session.runtimeSessionId,
    runtime: 'opencode',
    activity: isActive ? 'active' : 'idle',
    createdAt: session.createdAt,
    lastActivityAt: session.lastDataAt,
    model: session.model,
    resolvedModel: session.eventSummary?.resolvedModel ?? null,
    failureCategory: session.eventSummary?.failureCategory ?? null,
    failureReason: session.failureReason,
    toolCallCount: session.eventSummary?.toolCallCount ?? null,
    toolErrorCount: session.eventSummary?.toolErrorCount ?? null,
    workflowRunId: session.workflowRunId,
    sessionName: session.sessionName,
    contextRefs: { issueNumber },
    usage: session.usage,
    recoveryAvailable: !isActive,
    currentTurnId: isActive ? 'turn-active' : null,
    inputs: null,
    turns: isActive ? [{ id: 'turn-active', sequence: 1, inputIds: [], status: 'executing' }] : null,
  }
}

function makeUnifiedGenericSummary(session: ReturnType<typeof makeGenericRunningSession>) {
  return {
    id: session.sessionId,
    source: 'agent-launch',
    runtimeSessionId: session.runtimeSessionId,
    runtime: session.runtime,
    activity: 'active',
    createdAt: session.createdAt,
    lastActivityAt: session.lastActivityAt,
    model: null,
    resolvedModel: session.resolvedModel,
    failureCategory: session.failureCategory,
    failureReason: null,
    toolCallCount: session.toolCallCount,
    toolErrorCount: session.toolErrorCount,
    agentId: session.agentId,
    agentName: session.agentName,
    contextRefs: null,
    usage: session.usage,
    recoveryAvailable: false,
    currentTurnId: 'turn-generic',
    inputs: null,
    turns: [{ id: 'turn-generic', sequence: 1, inputIds: [], status: 'executing' }],
  }
}

interface CoderSessionFixture {
  session?: WorkflowRunSession
  compactError?: string
  genericSession?: ReturnType<typeof makeGenericRunningSession>
}

async function mockCoderSessionApi(page: Page, fixture: CoderSessionFixture = {}) {
  const session = fixture.session ?? makeCompactViewportSession()

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
    if (method === 'GET' && path === `/projects/${project.id}/sessions/${session.id}`) {
      return route.fulfill({ json: apiResponse(makeUnifiedWorkflowSummary(session)) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/sessions/${session.id}/transcript`) {
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
    if (fixture.genericSession && method === 'GET' && path === `/projects/${project.id}/agent-sessions/${genericSessionId}`) {
      return route.fulfill({ json: apiResponse(fixture.genericSession) })
    }
    if (fixture.genericSession && method === 'GET' && path === `/projects/${project.id}/agent-sessions/${genericSessionId}/transcript`) {
      return route.fulfill({ json: apiResponse(makeCompactViewportTranscript()) })
    }
    if (fixture.genericSession && method === 'GET' && path === `/projects/${project.id}/sessions/${genericSessionId}`) {
      return route.fulfill({ json: apiResponse(makeUnifiedGenericSummary(fixture.genericSession)) })
    }
    if (fixture.genericSession && method === 'GET' && path === `/projects/${project.id}/sessions/${genericSessionId}/transcript`) {
      return route.fulfill({ json: apiResponse(makeCompactViewportTranscript()) })
    }
    if (fixture.compactError && method === 'POST' && path === `/projects/${project.id}/agent-sessions/${session.id}/compact`) {
      return route.fulfill({ status: 409, json: { success: false, error: fixture.compactError } })
    }
    if (fixture.genericSession && method === 'POST' && path === `/projects/${project.id}/agent-sessions/${genericSessionId}/stop`) {
      return route.fulfill({ json: apiResponse({ state: 'cancelled' }) })
    }

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
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

const compactViewports = [
  { width: 375, height: 667 },
  { width: 320, height: 568 },
] as const

async function openIssueSession(page: Page, viewport: { width: number; height: number }, fixture?: CoderSessionFixture) {
  await page.setViewportSize(viewport)
  await mockCoderSessionApi(page, fixture)
  await page.goto(`/${project.name}/sessions/session-${sessionName}`)
}

async function expectReachableAboveMobileNav(page: Page, locator: Locator, label: string) {
  const control = await boxOf(page, locator)
  const nav = await navBox(page)
  const viewport = await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight }))

  expect(control.x, `${label} starts inside the viewport`).toBeGreaterThanOrEqual(0)
  expect(control.y, `${label} starts inside the viewport`).toBeGreaterThanOrEqual(0)
  expect(control.x + control.width, `${label} ends inside the viewport`).toBeLessThanOrEqual(viewport.width)
  expect(control.y + control.height, `${label} ends above the mobile navigation`).toBeLessThanOrEqual(nav.y + 1)
}

async function expectTranscriptScrollsIndependently(page: Page) {
  const transcript = page.getByTestId('session-transcript-scroll-container')
  const header = page.getByTestId('session-header')
  const stickyTitle = page.getByTestId('session-sticky-title')
  const composer = page.getByTestId('session-followup-composer')

  await expect(transcript).toBeVisible()
  await expect(header).toBeVisible()
  await expect(composer).toBeVisible()
  await expect(stickyTitle).toHaveCount(0)

  const [transcriptBox, headerBefore, composerBefore, documentScrollBefore] = await Promise.all([
    boxOf(page, transcript),
    boxOf(page, header),
    boxOf(page, composer),
    page.evaluate(() => window.scrollY),
  ])
  const metrics = await transcript.evaluate((node) => ({
    height: node.getBoundingClientRect().height,
    clientHeight: node.clientHeight,
    scrollHeight: node.scrollHeight,
  }))

  expect(metrics.height, 'transcript keeps the compact 120px reading floor').toBeGreaterThanOrEqual(120)
  expect(metrics.scrollHeight, 'transcript has overflow to read').toBeGreaterThan(metrics.clientHeight)

  await transcript.evaluate((node) => {
    node.scrollTop = Math.max(1, Math.floor((node.scrollHeight - node.clientHeight) / 2))
    node.dispatchEvent(new Event('scroll'))
  })
  await expect.poll(() => transcript.evaluate((node) => node.scrollTop)).toBeGreaterThan(0)
  await expect(stickyTitle).toBeVisible()

  const [headerAfter, stickyBefore, composerAfter, documentScrollAfter] = await Promise.all([
    boxOf(page, header),
    boxOf(page, stickyTitle),
    boxOf(page, composer),
    page.evaluate(() => window.scrollY),
  ])
  expect(headerAfter.y, 'outer header scrolls with transcript content').toBeLessThan(headerBefore.y)
  expect(headerAfter.y + headerAfter.height, 'outer header leaves the transcript viewport').toBeLessThanOrEqual(transcriptBox.y + 1)
  expect(Math.abs(stickyBefore.y - transcriptBox.y), 'sticky identity takes over at the transcript top').toBeLessThanOrEqual(1)
  expect(Math.abs(composerAfter.y - composerBefore.y), 'composer remains fixed while transcript scrolls').toBeLessThanOrEqual(1)
  expect(documentScrollAfter, 'document does not scroll with the transcript').toBe(documentScrollBefore)

  await transcript.evaluate((node) => {
    node.scrollTop = node.scrollHeight - node.clientHeight
    node.dispatchEvent(new Event('scroll'))
  })
  await expect.poll(() => transcript.evaluate((node) => node.scrollTop)).toBeGreaterThan(metrics.clientHeight)

  const stickyAfter = await boxOf(page, stickyTitle)
  expect(Math.abs(stickyAfter.y - stickyBefore.y), 'sticky identity remains pinned during further transcript scrolling').toBeLessThanOrEqual(1)
}

async function expectNoDocumentHorizontalOverflow(page: Page) {
  const metrics = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }))
  expect(metrics.scrollWidth).toBeLessThanOrEqual(metrics.clientWidth)
}

test.describe('Coder Session compact viewport pixel verification', () => {
  for (const viewport of compactViewports) {
    test(`preserves a readable transcript and pins identity after the header scrolls out at ${viewport.width}x${viewport.height}`, async ({ page }) => {
      await openIssueSession(page, viewport)
      await expectTranscriptScrollsIndependently(page)
    })

    test(`keeps Compact and Reset recovery controls above the mobile navigation at ${viewport.width}x${viewport.height}`, async ({ page }) => {
      await openIssueSession(page, viewport)
      await expectReachableAboveMobileNav(page, page.getByTestId('session-recovery-compact'), 'Compact')
      await expectReachableAboveMobileNav(page, page.getByTestId('session-recovery-reset'), 'Reset')
    })

    test(`renders the active follow-up form above the mobile navigation at ${viewport.width}x${viewport.height}`, async ({ page }) => {
      const runningSession: WorkflowRunSession = {
        ...makeCompactViewportSession(),
        status: 'active',
        completedAt: null,
        lastDataAt: '2026-06-12T10:05:30.000Z',
      }
      await openIssueSession(page, viewport, { session: runningSession })

      const composer = page.getByTestId('session-followup-composer')
      const input = page.getByTestId('session-followup-input')
      const send = page.getByTestId('session-followup-send')
      await expect(input).toBeVisible()
      await expect(send).toBeVisible()
      await expectReachableAboveMobileNav(page, composer, 'follow-up composer')
      await expectReachableAboveMobileNav(page, input, 'follow-up input')
      await expectReachableAboveMobileNav(page, send, 'follow-up send button')
    })

    test(`keeps transcript content clear of the mobile navigation at ${viewport.width}x${viewport.height}`, async ({ page }) => {
      await openIssueSession(page, viewport)
      await expectReachableAboveMobileNav(page, page.getByTestId('session-transcript-scroll-container'), 'transcript')
    })

    test(`keeps the generic session stop control reachable at ${viewport.width}x${viewport.height}`, async ({ page }) => {
      await page.setViewportSize(viewport)
      await mockCoderSessionApi(page, { genericSession: makeGenericRunningSession() })
       await page.goto(`/${project.name}/sessions/${genericSessionId}`)

      const stop = page.getByTestId('session-stop-trigger')
      await expectReachableAboveMobileNav(page, stop, 'stop session')
      await stop.click()
      await expect(page.getByTestId('session-cancel-alert')).toBeVisible()
    })
  }

  test('keeps a long recovery error readable without horizontal overflow at 320x568', async ({ page }) => {
    const recoveryError = 'The recovery request was rejected because a stale execution lease still owns this session and the current runner must release it before compaction can continue.'
    await openIssueSession(page, { width: 320, height: 568 }, { compactError: recoveryError })

    const compact = page.getByTestId('session-recovery-compact')
    const reset = page.getByTestId('session-recovery-reset')
    await compact.click()

    const error = page.getByTestId('session-recovery-error')
    await expect(error).toHaveText(recoveryError)
    await expectNoDocumentHorizontalOverflow(page)
    await expectReachableAboveMobileNav(page, compact, 'Compact after recovery error')
    await expectReachableAboveMobileNav(page, reset, 'Reset after recovery error')
    await expectReachableAboveMobileNav(page, error, 'recovery error')
  })
})
