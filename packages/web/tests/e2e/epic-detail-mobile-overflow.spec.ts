import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-epic-mobile-e2e',
  name: 'epic-mobile-e2e-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const LONG_TITLE = 'EpicDetailPageMobileHeaderTitleWithAnUnbrokenEnglishTokenThatMustWrapInsideTheReadableColumnAtThreeHundredTwentyPixels'
const LONG_DESCRIPTION = 'EpicDetailPageMobileHeaderDescriptionWithAnUnbrokenEnglishTokenThatMustWrapInsideTheDescriptionColumnAtThreeHundredTwentyPixels'

type EpicStatus = 'idle' | 'running' | 'done' | 'closed'

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeIssue(number: number, overrides: Record<string, unknown> = {}) {
  return {
    id: `issue-${number}`,
    number,
    title: `Linked issue ${number}`,
    status: 'backlog',
    stage: 'plan',
    health: 'active',
    priority: 'p2',
    canStart: true,
    startBlocker: null,
    prerequisiteNumbers: [],
    externalPrerequisites: [],
    isDraft: false,
    blocker: null,
    projectId: project.id,
    labels: {},
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
    ...overrides,
  }
}

function makeEpic(status: EpicStatus) {
  const linkedIssues = [
    makeIssue(1, { title: 'Root linked issue', status: 'done', stage: 'done', health: 'done', canStart: false }),
    makeIssue(2, { title: 'Dependent linked issue', prerequisiteNumbers: [1] }),
  ]

  return {
    id: `epic-${status}`,
    number: status === 'running' ? 7 : status === 'idle' ? 8 : status === 'done' ? 9 : 10,
    title: LONG_TITLE,
    description: LONG_DESCRIPTION,
    priority: 'p1',
    status,
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
    progress: {
      deliveredCount: status === 'done' || status === 'closed' ? 2 : 1,
      totalIssueCount: 2,
      blockedIssues: [],
      activeIssues: status === 'running' ? [{ id: 'issue-2', number: 2, title: 'Dependent linked issue', health: 'active' }] : [],
      nextIssue: status === 'done' || status === 'closed' ? null : { id: 'issue-2', number: 2, title: 'Dependent linked issue' },
      nextIssueReason: null,
      readyToMarkDone: status === 'done' || status === 'closed',
    },
    linkedIssues,
  }
}

async function mockEpicApi(page: Page) {
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
      return route.fulfill({ json: apiResponse([makeIssue(3, { title: 'Available issue' })]) })
    }
    if (method === 'GET' && path.startsWith(`/projects/${project.id}/epics/`)) {
      const id = decodeURIComponent(path.split('/').at(-1) ?? '')
      const status = id.replace('epic-', '') as EpicStatus
      return route.fulfill({ json: apiResponse(makeEpic(status)) })
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

test.describe('Epic detail mobile overflow', () => {
  for (const width of [320, 390, 430]) {
    for (const status of ['running', 'idle', 'done', 'closed'] as const) {
      test(`does not overflow at ${width}px for a ${status} epic with unbroken title and description`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 })
        await mockEpicApi(page)

        await page.goto(`/${project.name}/epics/epic-${status}`)
        await expect(page.getByRole('heading', { name: LONG_TITLE })).toBeVisible()
        await expect(page.getByTestId('epic-description')).toContainText(LONG_DESCRIPTION)

        await expectNoHorizontalOverflow(page)

        await page.getByTestId('linked-issues-view-graph').click()
        await expect(page.getByTestId('epic-dep-graph-canvas')).toBeVisible()
        await expectNoHorizontalOverflow(page)
      })
    }
  }
})
