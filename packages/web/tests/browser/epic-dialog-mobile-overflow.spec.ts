import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-epic-dialog-mobile-browser',
  name: 'epic-dialog-mobile-browser-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const epic = {
  number: 201,
  title: 'Epic edit dialog with a title that remains readable on narrow screens',
  description: Array.from(
    { length: 18 },
    (_, index) => `Description paragraph ${index + 1} keeps the form scrollable without widening the viewport.`,
  ).join('\n\n'),
  priority: 'p2',
  status: 'idle',
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
  progress: {
    deliveredCount: 0,
    totalIssueCount: 1,
    blockedIssues: [],
    activeIssues: [],
      nextIssue: { number: 201, title: 'Ready issue' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
  linkedIssues: [],
}

function apiResponse(data: unknown) {
  return { success: true, data }
}

async function mockEpicDialogApi(page: Page) {
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
      return route.fulfill({ json: apiResponse([epic]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/epics/${epic.number}`) {
      return route.fulfill({ json: apiResponse(epic) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/epics/${epic.number}/events`) {
      return route.fulfill({ json: apiResponse([]) })
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

async function expectCreateDialogControlsInsideViewport(page: Page) {
  await expectBoxInsideViewport(page, page.getByTestId('epic-create-scroll-region'), 'Create epic scroll region')
  await expectBoxInsideViewport(page, page.getByTestId('epic-create-footer'), 'Create epic footer')
  await expectBoxInsideViewport(page, page.getByTestId('epic-create-cancel'), 'Create epic cancel action')
  await expectBoxInsideViewport(page, page.getByTestId('epic-create-submit'), 'Create epic submit action')
}

async function expectEditDialogControlsInsideViewport(page: Page) {
  await expectBoxInsideViewport(page, page.getByTestId('edit-epic-scroll-region'), 'Edit epic scroll region')
  await expectBoxInsideViewport(page, page.getByTestId('edit-epic-footer'), 'Edit epic footer')
  await expectBoxInsideViewport(page, page.getByTestId('edit-epic-cancel'), 'Edit epic cancel action')
  await expectBoxInsideViewport(page, page.getByTestId('edit-epic-submit'), 'Edit epic submit action')
}

test.describe('Epic dialogs mobile overflow', () => {
  for (const width of [320, 390, 430]) {
    test(`create and edit controls stay inside the ${width}px viewport`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 })
      await mockEpicDialogApi(page)

      await page.goto(`/${project.name}/epics`)
      await expect(page.getByRole('heading', { name: 'Epics', level: 1 }).nth(1)).toBeVisible()
      await page.getByRole('button', { name: 'New Epic' }).click()
      await expect(page.getByTestId('epic-create-dialog')).toBeVisible()

      await expectNoHorizontalOverflow(page)
      await expectCreateDialogControlsInsideViewport(page)

      await page.getByTestId('epic-create-cancel').click()
      await expect(page.getByTestId('epic-create-dialog')).toBeHidden()

      await page.goto(`/${project.name}/epics/${epic.number}`)
      await expect(page.getByRole('heading', { name: epic.title })).toBeVisible()
      await page.getByTestId('edit-epic-button').click()
      await expect(page.getByTestId('edit-epic-dialog')).toBeVisible()

      await expectNoHorizontalOverflow(page)
      await expectEditDialogControlsInsideViewport(page)
    })
  }
})
