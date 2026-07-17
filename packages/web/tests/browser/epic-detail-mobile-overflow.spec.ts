import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-epic-mobile-browser',
  name: 'epic-mobile-browser-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const LONG_TITLE = 'EpicDetailPageMobileHeaderTitleWithAnUnbrokenEnglishTokenThatMustWrapInsideTheReadableColumnAtThreeHundredTwentyPixels'
const LONG_DESCRIPTION = 'EpicDetailPageMobileHeaderDescriptionWithAnUnbrokenEnglishTokenThatMustWrapInsideTheDescriptionColumnAtThreeHundredTwentyPixels'
const LONG_LINKED_ISSUE_TITLE = 'LinkedIssueRowMobileOverflowTitleWithAnUnbrokenTokenThatMustWrapInsideTheTaskLineAtThreeHundredTwentyPixels'
const FULL_DESCRIPTION = [
  LONG_DESCRIPTION,
  ...Array.from({ length: 24 }, (_, index) => `Background paragraph ${index + 1} that should stay below the summary area.`),
].join('\n\n')

type EpicStatus = 'idle' | 'running' | 'done' | 'closed'

const EPIC_NUMBER_BY_STATUS: Record<EpicStatus, number> = {
  running: 7,
  idle: 8,
  done: 9,
  closed: 10,
}

const LINKED_ROWS_EPIC_NUMBER = 11

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeIssue(number: number, overrides: Record<string, unknown> = {}) {
  return {
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

function makeEpic(status: EpicStatus, overrides: Record<string, unknown> = {}) {
  const linkedIssues = overrides.linkedIssues ?? [
    makeIssue(1, { title: 'Root linked issue', status: 'done', stage: 'done', health: 'done', canStart: false }),
    makeIssue(2, { title: 'Dependent linked issue', prerequisiteNumbers: [1] }),
  ]

  return {
    number: EPIC_NUMBER_BY_STATUS[status],
    title: LONG_TITLE,
    description: FULL_DESCRIPTION,
    priority: 'p1',
    status,
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
    progress: {
      deliveredCount: status === 'done' || status === 'closed' ? 2 : 1,
      totalIssueCount: 2,
      blockedIssues: [],
      activeIssues: status === 'running' ? [{ number: 2, title: 'Dependent linked issue', health: 'active' }] : [],
      nextIssue: status === 'done' || status === 'closed' ? null : { number: 2, title: 'Dependent linked issue' },
      nextIssueReason: null,
      readyToMarkDone: status === 'done' || status === 'closed',
    },
    linkedIssues,
    ...overrides,
  }
}

async function mockEpicApi(page: Page) {
  await page.route('**/hubs/events**', route => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const apiIndex = url.pathname.indexOf('/api/')
    const path = apiIndex >= 0 ? url.pathname.slice(apiIndex + '/api'.length) : url.pathname.replace(/^\/api/, '')
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
    const epicPath = `/projects/${project.id}/epics/`
    if (method === 'GET' && path.startsWith(epicPath) && path.endsWith('/events')) {
      return route.fulfill({ json: apiResponse([]) })
    }
    if (method === 'GET' && path.startsWith(epicPath)) {
      const rawNumber = decodeURIComponent(path.slice(epicPath.length))
      if (rawNumber.includes('/')) {
        return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
      }
      const epicNumber = Number(rawNumber)
      if (epicNumber === LINKED_ROWS_EPIC_NUMBER) {
        return route.fulfill({
          json: apiResponse(makeEpic('idle', {
            number: LINKED_ROWS_EPIC_NUMBER,
            linkedIssues: [
              makeIssue(1, {
                title: LONG_LINKED_ISSUE_TITLE,
                status: 'in_progress',
                stage: 'build',
                canStart: false,
              }),
              makeIssue(2, {
                title: `${LONG_LINKED_ISSUE_TITLE}Dependent`,
                canStart: false,
                startBlocker: { kind: 'waiting-for', issue: { number: 1, title: LONG_LINKED_ISSUE_TITLE } },
                prerequisiteNumbers: [1],
              }),
            ],
          })),
        })
      }
      const status = (Object.entries(EPIC_NUMBER_BY_STATUS) as Array<[EpicStatus, number]>)
        .find(([, number]) => number === epicNumber)?.[0]
      if (!status) {
        return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
      }
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

async function expectLinkedIssueRowsFitViewport(page: Page) {
  const rows = page.getByTestId('linked-issue-row')
  const count = await rows.count()
  expect(count).toBeGreaterThan(0)

  for (let index = 0; index < count; index += 1) {
    await expectBoxInsideViewport(page, rows.nth(index), `Linked issue row ${index + 1}`)
  }
}

const lifecycleActionTestId: Record<EpicStatus, string> = {
  idle: 'start-epic-trigger',
  running: 'pause-epic-trigger',
  done: 'reopen-epic-trigger',
  closed: 'reopen-epic-trigger',
}

async function expectSummaryVisibleBeforeOverview(page: Page, viewportHeight: number) {
  const summary = page.getByTestId('summary-grid')
  const overview = page.getByTestId('overview-card')

  await expect(summary).toBeVisible()
  await expect(overview).toBeVisible()
  await expect(summary.getByText('Progress', { exact: true })).toBeVisible()
  await expect(summary.getByText('Current Activity', { exact: true })).toBeVisible()
  await expect(summary.getByText('Next Issue', { exact: true })).toBeVisible()
  await expect(summary.getByText('1 / 2')).toBeVisible()
  await expect(summary.getByRole('link', { name: /#2 Dependent linked issue/ })).toBeVisible()

  const summaryBox = await summary.boundingBox()
  const overviewBox = await overview.boundingBox()
  expect(summaryBox).not.toBeNull()
  expect(overviewBox).not.toBeNull()
  const summaryBottom = summaryBox!.y + summaryBox!.height
  expect(summaryBox!.y).toBeGreaterThanOrEqual(0)
  expect(summaryBottom).toBeLessThanOrEqual(viewportHeight)
  expect(summaryBottom).toBeLessThan(overviewBox!.y)
}

test.describe('Epic detail mobile overflow', () => {
  for (const width of [320, 390, 430]) {
    for (const status of ['running', 'idle', 'done', 'closed'] as const) {
      test(`does not overflow at ${width}px for a ${status} epic with unbroken title and description`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 })
        await mockEpicApi(page)

        await page.goto(`/${project.name}/epics/${EPIC_NUMBER_BY_STATUS[status]}`)
        await expect(page.getByRole('heading', { name: LONG_TITLE })).toBeVisible()
        await expect(page.getByTestId('epic-description')).toContainText(LONG_DESCRIPTION)

        await expectNoHorizontalOverflow(page)
        await expectBoxInsideViewport(page, page.getByTestId('edit-epic-button'), 'Edit epic action')
        await expectBoxInsideViewport(
          page,
          page.getByTestId(lifecycleActionTestId[status]),
          `${status} lifecycle action`,
        )

        await page.getByTestId('linked-issues-view-graph').click()
        await expect(page.getByTestId('epic-dep-graph-canvas')).toBeVisible()
        await expectNoHorizontalOverflow(page)
      })
    }
  }

  for (const width of [320, 390, 430]) {
    test(`linked issue rows do not overflow at ${width}px with long titles and blocker copy`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 })
      await mockEpicApi(page)

      await page.goto(`/${project.name}/epics/${LINKED_ROWS_EPIC_NUMBER}`)
      await expect(page.getByTestId('linked-issue-title').first()).toContainText(LONG_LINKED_ISSUE_TITLE)
      await expect(page.getByText('Another issue is in progress')).toBeVisible()

      await expectLinkedIssueRowsFitViewport(page)
      await expectNoHorizontalOverflow(page)
    })
  }
})

test.describe('Epic detail summary first fold', () => {
  for (const viewport of [
    { label: 'mobile 390px', width: 390, height: 900 },
    { label: 'desktop', width: 1280, height: 900 },
  ]) {
    test(`shows progress, current activity, and next issue before overview on ${viewport.label}`, async ({ page }) => {
      await page.setViewportSize({ width: viewport.width, height: viewport.height })
      await mockEpicApi(page)

      await page.goto(`/${project.name}/epics/${EPIC_NUMBER_BY_STATUS.running}`)
      await expect(page.getByRole('heading', { name: LONG_TITLE })).toBeVisible()
      await expect(page.getByTestId('epic-description')).toContainText(LONG_DESCRIPTION)

      await expectSummaryVisibleBeforeOverview(page, viewport.height)
    })
  }
})
