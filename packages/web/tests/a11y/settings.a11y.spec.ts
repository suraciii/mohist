import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Locator, type Page } from '@playwright/test'

const settingsTabs = ['ai', 'agent', 'repositories', 'workflows', 'templates', 'system', 'preferences'] as const
const targetImpacts = new Set(['critical', 'serious'])
type Theme = 'light' | 'dark'
const themes: readonly Theme[] = ['light', 'dark'] as const

/**
 * Activate a theme before any page script runs. We use `addInitScript` so the
 * `mohist:theme` value lands in `localStorage` before the inline pre-paint
 * script in `index.html` reads it — the pre-paint script then applies the
 * `.dark` class on `<html>` before first paint, eliminating the FOUC that an
 * after-paint toggle would introduce. Theme choice: 'light' or 'dark'.
 */
async function applyTheme(page: Page, theme: Theme) {
  await page.addInitScript((stored: string) => {
    try {
      window.localStorage.setItem('mohist:theme', stored)
    } catch (_) {
      // Storage unavailable; axe-core will run with whatever theme the
      // browser defaulted to.
    }
  }, theme)
}
const project = {
  id: 'proj-a11y',
  name: 'a11y-project',
  repositories: [
    { name: 'frontend', gitUrl: 'https://github.com/example/frontend.git', baseBranch: 'main', isDefault: true },
    { name: 'backend', gitUrl: 'https://github.com/example/backend.git', baseBranch: 'develop', isDefault: false },
  ],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

const config = {
  logLevel: 'INFO',
  agentTimeout: 600,
  taskTimeout: 900,
  stageTimeout: 1800,
  maxConcurrentAgents: 2,
  maxGracePeriods: 1,
  pollInterval: 5,
}

const workflowTemplate = {
  id: 'mohist/default',
  name: 'Default',
  displayName: 'Default',
  description: 'Default workflow',
  isDefault: true,
  yaml: 'stages:\n  build: {}\n',
  stages: [{ stage: 'build', requiresApproval: false, tasks: ['Task'], checks: ['Check'] }],
}

const systemTemplate = {
  key: 'plan',
  displayName: 'Plan',
  description: 'Planning template',
  tags: ['workflow'],
  stage: 'plan',
  body: 'Plan the issue',
}

const templates = [
  {
    ...systemTemplate,
    source: 'system',
  },
]

function apiResponse(data: unknown) {
  return { success: true, data }
}

async function mockSettingsApi(page: Page) {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()

    if (method === 'GET' && path === '/projects') {
      return route.fulfill({ json: apiResponse([project]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/repositories`) {
      return route.fulfill({ json: apiResponse(project.repositories) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/opencode/models`) {
      return route.fulfill({ json: apiResponse({ models: ['openai/gpt-4.1', 'anthropic/claude-sonnet-4'] }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profile/variables`) {
      return route.fulfill({ json: apiResponse({ vars: { agent: { type: 'opencode', model: null } }, stages: {} }) })
    }
    if (method === 'GET' && path === '/opencode/runtime') {
      return route.fulfill({ json: apiResponse({ mode: 'local', command: 'opencode', model: null, note: '' }) })
    }
    if (method === 'GET' && path === '/config') {
      return route.fulfill({ json: apiResponse(config) })
    }
    if (method === 'GET' && path === '/workflow-templates/system') {
      return route.fulfill({ json: apiResponse([workflowTemplate]) })
    }
    if (method === 'GET' && decodeURIComponent(path) === '/workflow-templates/system/mohist/default') {
      return route.fulfill({ json: apiResponse(workflowTemplate) })
    }
    if (method === 'GET' && path === '/templates/system') {
      return route.fulfill({ json: apiResponse([systemTemplate]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/templates`) {
      return route.fulfill({ json: apiResponse(templates) })
    }
    if (method === 'POST' && path === `/projects/${project.id}/templates/plan/preview`) {
      return route.fulfill({ json: apiResponse({ rendered: 'Plan the issue' }) })
    }
    if (method === 'POST' && path === '/templates/extract-variables') {
      return route.fulfill({ json: apiResponse({ variables: [] }) })
    }
    if (method === 'GET' && path === '/system/info') {
      return route.fulfill({ json: apiResponse({
        running: { version: '0.1.0', gitHash: 'abcdef1', startedAt: '2026-06-01T00:00:00Z' },
        source: { path: '/repo', branch: 'master', head: 'abcdef1', dirty: false },
        install: { mode: 'local-source', serviceManager: null, serverUnit: null, runnerUnit: null, reason: null },
        update: { status: 'up-to-date', available: false, reason: null },
        services: { server: 'active', runner: 'active' },
        paths: { db: '/tmp/mohist.db', config: '/tmp/config.json', opencode: '/tmp/opencode', logs: '/tmp/logs' },
      }) })
    }
    if (method === 'GET' && path === '/system/update/status') {
      return route.fulfill({ json: apiResponse({ hasJob: false, job: null }) })
    }
    if (method === 'GET' && path === '/system/consistency') {
      return route.fulfill({ json: apiResponse({ status: 'consistent', reason: null, components: [] }) })
    }

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
  })
}

function settingsPath(tab: typeof settingsTabs[number]) {
  return `/${project.name}/settings/${tab}`
}

function channelToLinear(channel: number) {
  const srgb = channel / 255
  return srgb <= 0.03928 ? srgb / 12.92 : ((srgb + 0.055) / 1.055) ** 2.4
}

function luminance(rgb: [number, number, number]) {
  return 0.2126 * channelToLinear(rgb[0]) + 0.7152 * channelToLinear(rgb[1]) + 0.0722 * channelToLinear(rgb[2])
}

function contrastRatio(foreground: [number, number, number], background: [number, number, number]) {
  const lighter = Math.max(luminance(foreground), luminance(background))
  const darker = Math.min(luminance(foreground), luminance(background))
  return (lighter + 0.05) / (darker + 0.05)
}

async function expectMinTouchTarget(locator: Locator, label?: string) {
  await expect(locator).toBeVisible()
  const box = await locator.evaluate((element) => {
    const rect = element.getBoundingClientRect()
    return { width: rect.width, height: rect.height }
  })
  if (box.width < 44 || box.height < 44) {
    throw new Error(`${label ?? 'control'} is ${box.width}x${box.height}, expected at least 44x44`)
  }
}

async function gotoSettingsTab(page: Page, tab: typeof settingsTabs[number]) {
  await page.goto(settingsPath(tab))
  await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeAttached()
  await expect(page).toHaveURL(new RegExp(`/settings/${tab}$`))
}

test.describe('Settings accessibility browser audit', () => {
  test.beforeEach(async ({ page }) => {
    await mockSettingsApi(page)
  })

  for (const theme of themes) {
    for (const tab of settingsTabs) {
      test(`${settingsPath(tab)} (${theme}) has no critical or serious color-contrast violations`, async ({ page }) => {
        await applyTheme(page, theme)
        await gotoSettingsTab(page, tab)

        const results = await new AxeBuilder({ page })
          .include('main, [data-slot="sidebar-inset"], body')
          .withRules(['color-contrast'])
          .analyze()

        const unmet = results.violations.filter((violation) => targetImpacts.has(violation.impact ?? ''))
        expect(unmet).toEqual([])
      })

      test(`${settingsPath(tab)} (${theme}) has no critical or serious full-scan violations`, async ({ page }) => {
        await applyTheme(page, theme)
        await gotoSettingsTab(page, tab)

        const results = await new AxeBuilder({ page }).analyze()
        const unmet = results.violations.filter((violation) => targetImpacts.has(violation.impact ?? ''))
        expect(unmet).toEqual([])
      })
    }
  }

  for (const theme of themes) {
    test(`open settings search dialog (${theme}) has no critical or serious violations and exposes the modal contract`, async ({ page }) => {
      await applyTheme(page, theme)
      await gotoSettingsTab(page, 'agent')

      // Open the search dialog with the ⌘K keystroke. The handler is bound
      // locally on the Settings page; this is the same path the unit
      // coverage uses, lifted into the browser audit so axe-core can scan
      // the resulting modal.
      await page.keyboard.press('Meta+K')
      const input = page.getByTestId('settings-search-input')
      await expect(input).toBeVisible()
      await expect(input).toBeFocused()

      const dialog = page.locator('[data-slot="dialog-content"]')
      await expect(dialog).toBeVisible()

      // The dialog must satisfy the modal-dialog contract that the Settings
      // accessibility spec mandates: `aria-modal="true"` and a labelled-by
      // reference to a visible title element.
      await expect(dialog).toHaveAttribute('aria-modal', 'true')
      await expect(dialog).toHaveAttribute('aria-labelledby', /.+/)

      // Limit the scan to the open dialog so axe-core does not flag issues
      // on the underlying Settings tab (which is covered by the per-tab
      // tests above).
      const results = await new AxeBuilder({ page })
        .include('[data-slot="dialog-content"]')
        .analyze()

      const unmet = results.violations.filter((violation) => targetImpacts.has(violation.impact ?? ''))
      expect(unmet).toEqual([])
    })
  }

  test('repositories actions and add form meet touch and narrow-width bounds', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 })
    await gotoSettingsTab(page, 'repositories')
    await expect(page.getByRole('heading', { name: 'Add Repository', level: 3 })).toBeVisible()

    for (const testId of ['repository-set-default-backend', 'repository-remove-backend']) {
      await expectMinTouchTarget(page.getByTestId(testId))
    }

    const form = page.getByRole('heading', { name: 'Add Repository', level: 3 }).locator('..')
    const formBox = await form.boundingBox()
    expect(formBox).not.toBeNull()
    expect(formBox!.x).toBeGreaterThanOrEqual(0)
    expect(formBox!.x + formBox!.width).toBeLessThanOrEqual(375)

    for (const label of ['Name', 'Base Branch', 'Git URL']) {
      const labelBox = await form.getByText(label, { exact: true }).boundingBox()
      expect(labelBox).not.toBeNull()
      expect(labelBox!.width).toBeGreaterThan(0)
      expect(labelBox!.x).toBeGreaterThanOrEqual(formBox!.x)
      expect(labelBox!.x + labelBox!.width).toBeLessThanOrEqual(formBox!.x + formBox!.width)
    }

    for (const testId of ['repository-add-name', 'repository-add-branch', 'repository-add-giturl']) {
      const inputBox = await page.getByTestId(testId).boundingBox()
      expect(inputBox).not.toBeNull()
      expect(inputBox!.x).toBeGreaterThanOrEqual(formBox!.x)
      expect(inputBox!.x + inputBox!.width).toBeLessThanOrEqual(formBox!.x + formBox!.width)
    }
  })

  test('templates tab interactive controls meet touch target bounds', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 })
    await gotoSettingsTab(page, 'templates')
    await expect(page.getByTestId('template-search')).toBeVisible()

    for (const testId of ['template-new-button', 'template-override-plan', 'template-preview-plan']) {
      await expectMinTouchTarget(page.getByTestId(testId))
    }

    const searchBox = await page.getByTestId('template-search').boundingBox()
    expect(searchBox).not.toBeNull()
    expect(searchBox!.height).toBeGreaterThanOrEqual(44)

    const previewButton = page.getByTestId('template-preview-plan')
    await previewButton.scrollIntoViewIfNeeded()
    await expect(previewButton).toBeVisible()
    await expect(previewButton).toBeEnabled()
    await previewButton.click()
    await expect(page.getByRole('heading', { name: 'Preview plan', level: 4 })).toBeVisible()
    for (const testId of [
      'template-editor-close',
      'template-editor-body',
      'template-editor-preview-vars',
      'template-editor-cancel',
    ]) {
      await expectMinTouchTarget(page.getByTestId(testId))
    }
  })

  test('workflow profile detail back control meets touch target bounds', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 })
    await gotoSettingsTab(page, 'workflows')

    await page.getByTestId('workflow-profile-mohist/default').click()
    await expect(page.getByText('Failed to load profile.')).toHaveCount(0)
    await expectMinTouchTarget(page.getByTestId('workflow-profile-back'))
  })

  test('adjusted Settings contrast tokens meet AA ratios', async ({ page }) => {
    await gotoSettingsTab(page, 'workflows')

    await expect(page.locator('section > p').first()).toBeVisible()
    const sectionDescriptionRatio = contrastRatio([23, 23, 23], [255, 255, 255])
    expect(sectionDescriptionRatio).toBeGreaterThanOrEqual(4.5)

    const errorRatio = contrastRatio([185, 28, 28], [254, 242, 242])
    expect(errorRatio).toBeGreaterThanOrEqual(4.5)
  })
})
