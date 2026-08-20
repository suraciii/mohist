import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-epic-list-mobile-browser',
  name: 'epic-list-mobile-browser-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const LONG_RUNNING_TITLE =
  'CurrentIssueWithAnUnbrokenLongTokenThatMustWrapInsideTheEpicListCardWithoutPushingTheMobileViewportWide'
const LONG_NEXT_TITLE =
  'QueuedNextIssueWithAnUnbrokenLongTokenThatMustWrapInsideTheReadyCardWithoutHidingTheIssueNumber'
const LONG_REASON =
  'WaitingBecauseExternalPrerequisiteWithAnUnbrokenLongTokenMustWrapInsideTheCardInsteadOfCreatingHorizontalOverflow'

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeEpic(overrides: Record<string, unknown>) {
  return {
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
    number: 101,
    title: 'Running Epic With A Long Title That Needs To Wrap On Mobile',
    status: 'running',
    progress: {
      deliveredCount: 1,
      totalIssueCount: 4,
      blockedIssues: [],
      activeIssues: [{ number: 12345, title: LONG_RUNNING_TITLE, health: 'active' }],
      nextIssue: null,
      nextIssueReason: 'Waiting for #12345 to complete',
      readyToMarkDone: false,
    },
  }),
  makeEpic({
    number: 102,
    title: 'Ready Epic With A Long Title That Needs To Wrap On Mobile',
    progress: {
      deliveredCount: 0,
      totalIssueCount: 3,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: { number: 67890, title: LONG_NEXT_TITLE },
      nextIssueReason: null,
      readyToMarkDone: false,
    },
  }),
  makeEpic({
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

async function expectBoxInsideViewport(page: Page, locator: ReturnType<Page['getByTestId']>, label: string) {
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

async function expectResponsiveControlsInsideViewport(page: Page) {
  await expectBoxInsideViewport(page, page.getByTestId('epic-list-toolbar'), 'Epic list toolbar')
  await expectBoxInsideViewport(page, page.getByTestId('epic-card-in-progress'), 'Running issue label')
  await expectBoxInsideViewport(
    page,
    page.getByTestId('epic-card-next').filter({ hasText: '#67890' }),
    'Next issue label',
  )
  await expectBoxInsideViewport(page, page.getByTestId('epic-card-start'), 'Start next issue action')
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
      await expectResponsiveControlsInsideViewport(page)
    })
  }
})
