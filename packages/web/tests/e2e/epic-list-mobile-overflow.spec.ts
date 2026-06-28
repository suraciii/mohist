import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-epic-list-mobile-e2e',
  name: 'epic-list-mobile-e2e-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const LONG_RUNNING_TITLE = 'CurrentIssueWithAnUnbrokenLongTokenThatMustWrapInsideTheEpicListCardWithoutPushingTheMobileViewportWide'
const LONG_NEXT_TITLE = 'QueuedNextIssueWithAnUnbrokenLongTokenThatMustWrapInsideTheReadyCardWithoutHidingTheIssueNumber'
const LONG_REASON = 'WaitingBecauseExternalPrerequisiteWithAnUnbrokenLongTokenMustWrapInsideTheCardInsteadOfCreatingHorizontalOverflow'

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeEpic(overrides: Record<string, unknown>) {
  return {
    id: 'epic-id',
    number: 1,
    title: 'Epic',
    description: 'desc',
    priority: 'p1',
    status: 'idle',
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
    progress: {
      deliveredCount: 0,
      totalIssueCount: 0,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: null,
      nextIssueReason: null,
      readyToMarkDone: false,
    },
    ...overrides,
  }
}

const epics = [
  makeEpic({
    id: 'epic-running-mobile',
    number: 101,
    title: 'Running Epic With A Long Title That Needs To Wrap On Mobile',
    status: 'running',
    progress: {
      deliveredCount: 1,
      totalIssueCount: 4,
      blockedIssues: [],
      activeIssues: [{ id: 'issue-12345', number: 12345, title: LONG_RUNNING_TITLE, health: 'active' }],
      nextIssue: null,
      nextIssueReason: 'Waiting for #12345 to complete',
      readyToMarkDone: false,
    },
  }),
  makeEpic({
    id: 'epic-ready-mobile',
    number: 102,
    title: 'Ready Epic With A Long Title That Needs To Wrap On Mobile',
    progress: {
      deliveredCount: 0,
      totalIssueCount: 3,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: { id: 'issue-67890', number: 67890, title: LONG_NEXT_TITLE },
      nextIssueReason: null,
      readyToMarkDone: false,
    },
  }),
  makeEpic({
    id: 'epic-waiting-mobile',
    number: 103,
    title: 'Waiting Epic With A Long Title That Needs To Wrap On Mobile',
    progress: {
      deliveredCount: 0,
      totalIssueCount: 2,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: null,
      nextIssueReason: LONG_REASON,
      readyToMarkDone: false,
    },
  }),
  makeEpic({
    id: 'epic-idle-mobile',
    number: 104,
    title: 'Idle Empty Epic With A Long Title That Needs To Wrap On Mobile',
    progress: {
      deliveredCount: 0,
      totalIssueCount: 0,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: null,
      nextIssueReason: null,
      readyToMarkDone: false,
    },
  }),
]

async function mockEpicListApi(page: Page) {
  await page.route('**/hubs/events**', route => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()

    if (method === 'GET' && path === '/projects') {
      return route.fulfill({ json: apiResponse([project]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/agent/status`) {
      return route.fulfill({ json: apiResponse({ running: false, activeAgents: [], capacity: { active: 0, max: 8 } }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues`) {
      return route.fulfill({ json: apiResponse([]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/epics`) {
      return route.fulfill({ json: apiResponse(epics) })
    }

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
  })
}

async function expectNoHorizontalOverflow(page: Page) {
  const metrics = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }))
  expect(metrics.scrollWidth).toBeLessThanOrEqual(metrics.clientWidth)
}

async function expectStateTextVisibleWithinViewport(page: Page) {
  const viewport = page.viewportSize()
  expect(viewport).not.toBeNull()
  const stateTexts = [
    page.getByTestId('epic-card-in-progress').filter({ hasText: '#12345' }),
    page.getByTestId('epic-card-next').filter({ hasText: '#67890' }),
  ]

  for (const stateText of stateTexts) {
    await expect(stateText).toBeVisible()
    const box = await stateText.boundingBox()
    expect(box).not.toBeNull()
    expect(box!.x).toBeGreaterThanOrEqual(0)
    expect(box!.x + box!.width).toBeLessThanOrEqual(viewport!.width)
  }

  for (const label of ['Running', 'Idle']) {
    const badge = page.locator('[data-slot="badge"]', { hasText: label }).first()
    await expect(badge).toBeVisible()
    const box = await badge.boundingBox()
    expect(box).not.toBeNull()
    expect(box!.x).toBeGreaterThanOrEqual(0)
    expect(box!.x + box!.width).toBeLessThanOrEqual(viewport!.width)
  }
}

test.describe('Epic list mobile overflow', () => {
  for (const width of [320, 390, 430]) {
    test(`does not overflow at ${width}px with all active presentation groups`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 })
      await mockEpicListApi(page)

      await page.goto(`/${project.name}/epics`)
      await expect(page.getByRole('heading', { name: 'Epics', level: 1 }).nth(1)).toBeVisible()
      await expect(page.getByTestId('epic-section-running')).toContainText('Running Epic')
      await expect(page.getByTestId('epic-section-ready')).toContainText('Ready Epic')
      await expect(page.getByTestId('epic-section-waiting')).toContainText(LONG_REASON)
      await expect(page.getByTestId('epic-section-idle')).toContainText('No linked issues')

      await expectNoHorizontalOverflow(page)
      await expectStateTextVisibleWithinViewport(page)
    })
  }
})
