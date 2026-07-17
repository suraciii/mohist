import { expect, test, type Locator, type Page } from '@playwright/test'

const project = {
  id: 'proj-issue-board-layout-browser',
  name: 'issue-board-layout-browser-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeIssue(number: number, overrides: Record<string, unknown> = {}) {
  return {
    number,
    title: `Issue ${number}`,
    status: 'backlog',
    workflowStage: 'plan',
    workflowStatus: 'running',
    workflowStageProgress: {
      stage: 'plan',
      total: 3,
      completed: 1,
      running: 1,
      failed: 0,
      currentTaskTitle: 'Layout task',
    },
    workflowRunId: `wr-${number}`,
    workflowProfileId: 'mohist/local',
    health: 'active',
    projectId: project.id,
    labels: { kind: 'layout' },
    priority: 'p1',
    risk: null,
    model: null,
    modelVariant: null,
    agentConfig: null,
    stageModels: {},
    stageModelVariants: {},
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
    comments: [],
    attachments: [],
    prerequisites: [],
    prerequisiteNumbers: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    feedback: [],
    ...overrides,
  }
}

const issues = [
  makeIssue(101, { status: 'backlog', title: 'Backlog board item' }),
  makeIssue(102, { status: 'in_progress', workflowStage: 'build', title: 'In progress board item' }),
  makeIssue(103, { status: 'done', workflowStage: 'done', workflowStatus: 'completed', health: 'done', title: 'Done board item' }),
  makeIssue(104, { status: 'cancelled', workflowStage: null, workflowStatus: null, health: 'cancelled', title: 'Cancelled board item' }),
  makeIssue(105, { status: 'backlog', health: 'interrupted', title: 'Mobile rerun board item' }),
]

async function mockIssueBoardApi(page: Page) {
  await page.route('**/hubs/events**', route => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()

    if (method === 'GET' && path === '/projects') {
      return route.fulfill({ json: apiResponse([project]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues`) {
      return route.fulfill({ json: apiResponse(issues) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/labels`) {
      return route.fulfill({ json: apiResponse(['kind']) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/agent/status`) {
      return route.fulfill({ json: apiResponse({ running: false, runnerAvailable: true, activeAgents: [], capacity: { active: 0, max: 8 } }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/runners`) {
      return route.fulfill({ json: apiResponse({ runners: [{ id: 'runner-layout', status: 'idle', slots: 1, active: 0 }] }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/inbox`) {
      return route.fulfill({ json: apiResponse([]) })
    }

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
  })
}

async function box(locator: Locator) {
  const value = await locator.boundingBox()
  expect(value).not.toBeNull()
  return value!
}

function boxesOverlap(a: { x: number; y: number; width: number; height: number }, b: { x: number; y: number; width: number; height: number }) {
  return a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y
}

test.describe('Issue board browser layout', () => {
  test('keeps desktop filters and core columns visible with the app sidebar', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    await mockIssueBoardApi(page)

    await page.goto(`/${project.name}/issues`)

    const boardRoot = page.getByTestId('kanban-board-root')
    const boardRow = page.getByTestId('kanban-board-row')
    const backlog = page.getByTestId('stage-column-backlog')
    const inProgress = page.getByTestId('stage-column-in_progress')
    const done = page.getByTestId('stage-column-done')
    const cancelledStub = page.getByTestId('cancelled-collapsed-stub')

    await expect(boardRoot).toBeVisible()
    await expect(page.getByTestId('search-input').first()).toBeVisible()
    await expect(page.getByTestId('priority-chip-p0').first()).toBeVisible()
    await expect(page.getByTestId('label-chip').first()).toBeVisible()
    await expect(page.getByTestId('sort-priority').first()).toBeVisible()
    await expect(backlog.getByText('Backlog board item')).toBeVisible()
    await expect(inProgress.getByText('In progress board item')).toBeVisible()
    await expect(done.getByText('Done board item')).toBeVisible()
    await expect(cancelledStub).toContainText('1')

    await expect(boardRow).toHaveClass(/overflow-x-auto/)
    await expect(boardRow).toHaveClass(/min-w-0/)
    for (const column of [backlog, inProgress, done]) {
      await expect(column).toHaveClass(/flex-1/)
      await expect(column).toHaveClass(/max-w-\[420px\]/)
    }

    const viewport = page.viewportSize()
    expect(viewport).not.toBeNull()
    const rowBox = await box(boardRow)
    const filterBox = await box(page.locator('.hidden.md\\:flex.flex-wrap'))
    const stubBox = await box(cancelledStub)
    expect(filterBox.y + filterBox.height).toBeLessThanOrEqual(viewport!.height)
    expect(rowBox.y).toBeLessThan(viewport!.height)
    expect(stubBox.width).toBeLessThanOrEqual(130)

    for (const locator of [backlog, inProgress, done]) {
      const columnBox = await box(locator)
      expect(columnBox.x).toBeGreaterThanOrEqual(rowBox.x)
      expect(columnBox.x + columnBox.width).toBeLessThanOrEqual(rowBox.x + rowBox.width)
      expect(columnBox.y).toBeGreaterThanOrEqual(0)
      expect(columnBox.y).toBeLessThan(viewport!.height)
    }
  })

  test('keeps mobile filters, tabs, cards, and primary action from overlapping', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 820 })
    await mockIssueBoardApi(page)

    await page.goto(`/${project.name}/issues`)

    const mobileFilter = page.locator('.md\\:hidden.px-3.py-2').first()
    const search = mobileFilter.getByTestId('search-input')
    const filterToggle = mobileFilter.getByTestId('mobile-filter-toggle')
    const tabStrip = page.getByTestId('mobile-stage-tab-backlog').locator('xpath=..')
    const cardList = page.getByTestId('issue-card').first().locator('xpath=..')
    const firstCard = page.getByTestId('issue-card').first()
    const rerunButton = firstCard.getByTestId('rerun-button')

    await expect(search).toBeVisible()
    await expect(filterToggle).toBeVisible()
    await filterToggle.click()
    await expect(page.getByTestId('mobile-filter-panel')).toBeVisible()
    await expect(tabStrip).toBeVisible()
    await expect(firstCard).toBeVisible()
    await expect(rerunButton).toBeVisible()

    const viewport = page.viewportSize()
    expect(viewport).not.toBeNull()
    const mobileFilterBox = await box(mobileFilter)
    const panelBox = await box(page.getByTestId('mobile-filter-panel'))
    const tabStripBox = await box(tabStrip)
    const cardListBox = await box(cardList)
    const cardBox = await box(firstCard)
    const actionBox = await box(rerunButton)

    expect(panelBox.x).toBeGreaterThanOrEqual(mobileFilterBox.x)
    expect(panelBox.x + panelBox.width).toBeLessThanOrEqual(mobileFilterBox.x + mobileFilterBox.width)
    expect(mobileFilterBox.y + mobileFilterBox.height).toBeLessThanOrEqual(tabStripBox.y)
    expect(tabStripBox.y + tabStripBox.height).toBeLessThanOrEqual(cardListBox.y)
    expect(boxesOverlap(mobileFilterBox, tabStripBox)).toBe(false)
    expect(boxesOverlap(tabStripBox, cardBox)).toBe(false)
    expect(boxesOverlap(tabStripBox, actionBox)).toBe(false)
    expect(cardBox.x).toBeGreaterThanOrEqual(0)
    expect(cardBox.x + cardBox.width).toBeLessThanOrEqual(viewport!.width)
    expect(actionBox.x).toBeGreaterThanOrEqual(cardBox.x)
    expect(actionBox.x + actionBox.width).toBeLessThanOrEqual(cardBox.x + cardBox.width)
  })
})
