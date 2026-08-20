import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-search-browser',
  name: 'search-browser-project',
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
  maxGracePeriods: 30,
  pollInterval: 30000,
}

const workflowTemplate = {
  id: 'mohist/local',
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

async function mockSettingsApi(page: Page, repositories = project.repositories) {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()

    if (method === 'GET' && path === '/projects') {
      return route.fulfill({ json: apiResponse([project]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/repositories`) {
      return route.fulfill({ json: apiResponse(repositories) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/opencode/models`) {
      return route.fulfill({
        json: apiResponse({
          models: ['openai/gpt-4.1', 'anthropic/claude-sonnet-4'],
          modelVariants: { 'anthropic/claude-sonnet-4': ['low', 'medium', 'high'] },
        }),
      })
    }
    if (method === 'GET' && path === `/projects/${project.id}/variables`) {
      return route.fulfill({ json: apiResponse({ vars: { agent: { type: 'opencode', model: null } }, stages: {} }) })
    }
    if (method === 'GET' && path === '/opencode/runtime') {
      return route.fulfill({ json: apiResponse({ mode: 'local', command: 'opencode', model: null, note: '' }) })
    }
    if (method === 'GET' && path === '/config') {
      return route.fulfill({ json: apiResponse(config) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profile/default`) {
      return route.fulfill({
        json: apiResponse({
          projectId: project.id,
          defaultWorkflowProfileId: 'mohist/local',
          disabledWorkflowProfileIds: [],
        }),
      })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profiles`) {
      return route.fulfill({
        json: apiResponse([
          {
            projectId: project.id,
            profileId: workflowTemplate.id,
            name: workflowTemplate.name,
            description: workflowTemplate.description,
            sourceProvenance: 'BuiltIn',
            isBuiltIn: true,
            definitionSource: workflowTemplate.yaml,
            agentRuntime: 'opencode',
          },
        ]),
      })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profiles/mohist/local`) {
      return route.fulfill({
        json: apiResponse({
          projectId: project.id,
          profileId: workflowTemplate.id,
          name: workflowTemplate.name,
          description: workflowTemplate.description,
          sourceProvenance: 'BuiltIn',
          isBuiltIn: true,
          definitionSource: workflowTemplate.yaml,
          agentRuntime: 'opencode',
          stages: workflowTemplate.stages,
        }),
      })
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
      return route.fulfill({
        json: apiResponse({
          running: { version: '0.1.0', gitHash: 'abcdef1', startedAt: '2026-06-01T00:00:00Z' },
          source: { path: '/repo', branch: 'master', head: 'abcdef1', dirty: false },
          install: { mode: 'local-source', serviceManager: null, serverUnit: null, runnerUnit: null, reason: null },
          update: { status: 'up-to-date', available: false, reason: null },
          services: { server: 'active', runner: 'active' },
          paths: { db: '/tmp/mohist.db', config: '/tmp/config.json', opencode: '/tmp/opencode', logs: '/tmp/logs' },
        }),
      })
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

async function gotoSettingsTab(
  page: Page,
  tab: 'ai' | 'agent' | 'repositories' | 'workflows' | 'templates' | 'system' | 'preferences',
) {
  await page.goto(`/${project.name}/settings/${tab}`)
  await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeAttached()
  await expect(page).toHaveURL(new RegExp(`/settings/${tab}$`))
}

test.describe('Settings search dialog in Chromium', () => {
  test('⌘K opens the dialog on a Settings tab', async ({ page }) => {
    await mockSettingsApi(page)
    await gotoSettingsTab(page, 'agent')

    // Ensure no dialog before the keystroke.
    await expect(page.getByTestId('settings-search-input')).toHaveCount(0)

    // Use Meta+K — Chromium on Linux still fires the keystroke regardless of
    // platform, and the handler accepts both Meta and Ctrl modifiers.
    await page.keyboard.press('Meta+K')

    const input = page.getByTestId('settings-search-input')
    await expect(input).toBeVisible()
    await expect(input).toBeFocused()
  })

  test('Ctrl+K opens the dialog on a Settings tab', async ({ page }) => {
    await mockSettingsApi(page)
    await gotoSettingsTab(page, 'preferences')

    await expect(page.getByTestId('settings-search-input')).toHaveCount(0)
    await page.keyboard.press('Control+K')

    await expect(page.getByTestId('settings-search-input')).toBeVisible()
  })

  test('does not open the dialog when ⌘K is pressed outside the Settings page', async ({ page }) => {
    await mockSettingsApi(page)
    await page.goto(`/${project.name}`)
    await expect(page.getByTestId('settings-search-input')).toHaveCount(0)

    await page.keyboard.press('Meta+K')

    await expect(page.getByTestId('settings-search-input')).toHaveCount(0)
  })

  test('search matches label, description, and placeholder but excludes current numeric values', async ({ page }) => {
    await mockSettingsApi(page)
    await gotoSettingsTab(page, 'agent')

    await page.keyboard.press('Meta+K')
    const input = page.getByTestId('settings-search-input')
    await expect(input).toBeVisible()

    // Match on label.
    await input.fill('timeout')
    await expect(page.getByTestId('settings-search-result-agent-runtime-timeout')).toBeVisible()

    // Match on description (no overlap with label).
    await input.fill('upper bound')
    await expect(page.getByTestId('settings-search-result-agent-runtime-maxConcurrent')).toBeVisible()

    // Match on placeholder (repositories tab is the only tab with placeholders).
    await input.fill('e.g. frontend')
    await expect(page.getByTestId('settings-search-result-repository-add-name')).toBeVisible()

    // Exclude live numeric values. The fixture seeds maxGracePeriods = 30
    // and pollInterval = 30000ms, both of which contain the substring "30".
    // A search for "30" must NOT match either of these fields just because
    // their current value happens to be 30.
    await input.fill('30')
    await expect(page.getByTestId('settings-search-result-agent-runtime-maxGracePeriods')).toHaveCount(0)
    await expect(page.getByTestId('settings-search-result-agent-runtime-pollInterval')).toHaveCount(0)
  })

  test('Enter on a highlighted result navigates to the owning tab and focuses the field', async ({ page }) => {
    await mockSettingsApi(page)
    await gotoSettingsTab(page, 'ai')

    await page.keyboard.press('Meta+K')
    const input = page.getByTestId('settings-search-input')
    await expect(input).toBeVisible()

    await input.fill('session timeout')

    // Highlight the only match.
    const result = page.getByTestId('settings-search-result-agent-runtime-timeout')
    await expect(result).toBeVisible()
    await result.hover()

    await page.keyboard.press('Enter')

    // Dialog closes; the URL switches to the owning tab.
    await expect(page.getByTestId('settings-search-input')).toHaveCount(0)
    await expect(page).toHaveURL(new RegExp(`/settings/agent$`))

    // The focus target receives keyboard focus with a visible :focus-visible
    // outline. We assert the active element id rather than reading CSS so the
    // test stays stable across theme changes.
    await expect(page.locator('#agent-runtime-timeout')).toBeFocused()

    // The native :focus-visible state should be set on the focused element
    // (Chromium honours the heuristic for keyboard-driven focus).
    const matchesFocusVisible = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null
      if (!el) return false
      return el.matches(':focus-visible')
    })
    expect(matchesFocusVisible).toBe(true)
  })

  test('Enter reveals collapsed stage model overrides before focusing a stage model field', async ({ page }) => {
    await mockSettingsApi(page)
    await gotoSettingsTab(page, 'agent')

    await page.keyboard.press('Meta+K')
    const input = page.getByTestId('settings-search-input')
    await expect(input).toBeVisible()

    await input.fill('plan stage model')
    const result = page.getByTestId('settings-search-result-settings-stage-model-plan')
    await expect(result).toBeVisible()
    await result.hover()

    await page.keyboard.press('Enter')

    await expect(page.getByTestId('settings-search-input')).toHaveCount(0)
    await expect(page).toHaveURL(new RegExp(`/settings/ai$`))
    await expect(page.locator('#settings-stage-model-plan')).toBeFocused()
  })

  test('Enter reveals the empty repository add form before focusing a repository field', async ({ page }) => {
    await mockSettingsApi(page, [])
    await gotoSettingsTab(page, 'agent')

    await page.keyboard.press('Meta+K')
    const input = page.getByTestId('settings-search-input')
    await expect(input).toBeVisible()

    await input.fill('repository name')
    const result = page.getByTestId('settings-search-result-repository-add-name')
    await expect(result).toBeVisible()
    await result.hover()

    await page.keyboard.press('Enter')

    await expect(page.getByTestId('settings-search-input')).toHaveCount(0)
    await expect(page).toHaveURL(new RegExp(`/settings/repositories$`))
    await expect(page.locator('#repository-add-name')).toBeFocused()
  })

  test('Esc closes the dialog without navigating', async ({ page }) => {
    await mockSettingsApi(page)
    await gotoSettingsTab(page, 'ai')

    await page.keyboard.press('Meta+K')
    const input = page.getByTestId('settings-search-input')
    await expect(input).toBeVisible()

    await input.fill('timeout')
    await page.keyboard.press('Escape')

    await expect(page.getByTestId('settings-search-input')).toHaveCount(0)
    await expect(page).toHaveURL(new RegExp(`/settings/ai$`))
  })

  test('overlay click closes the dialog without navigating', async ({ page }) => {
    await mockSettingsApi(page)
    await gotoSettingsTab(page, 'agent')

    await page.keyboard.press('Meta+K')
    const input = page.getByTestId('settings-search-input')
    await expect(input).toBeVisible()

    // Click on the dialog overlay (outside the inner content) to dismiss.
    await page.locator('[data-slot="dialog-overlay"]').click({ position: { x: 5, y: 5 } })

    await expect(page.getByTestId('settings-search-input')).toHaveCount(0)
    await expect(page).toHaveURL(new RegExp(`/settings/agent$`))
  })

  test('empty result renders "No matching settings"', async ({ page }) => {
    await mockSettingsApi(page)
    await gotoSettingsTab(page, 'agent')

    await page.keyboard.press('Meta+K')
    const input = page.getByTestId('settings-search-input')
    await expect(input).toBeVisible()

    await input.fill('zzz-no-such-setting-zzz')

    await expect(page.getByTestId('settings-search-empty')).toHaveText('No matching settings')
  })
})
