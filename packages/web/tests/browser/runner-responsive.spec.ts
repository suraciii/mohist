import { expect, test, type Page } from '@playwright/test'

const runnerId = 'runner-offline-known-definition-with-a-long-stable-id-0123456789'

function response(data: unknown) {
  return { success: true, data }
}

const offlineRunner = {
  identity: {
    id: runnerId,
    hostname: null,
    kind: null,
    component: null,
    sourceRevision: null,
    releaseId: null,
    generation: null,
  },
  presence: { state: 'offline', lastObservedAt: null },
  control: { state: 'disconnected', generation: null },
  admission: { state: 'blocked', reasonCodes: ['presence-offline', 'credential-revoked'] },
  capabilities: ['spec/*'],
  runtimes: [],
  capacity: { used: null, total: 4 },
  activeWorks: [],
  drain: null,
  nextActions: [
    {
      code: 'reenroll-runner',
      message: 'Re-enroll the Runner credential.',
      command: `mo install runner --repo-root <path> --runner-id ${runnerId}`,
    },
  ],
}

const liveRunner = {
  identity: {
    id: 'runner-draining-with-mixed-owner-work',
    hostname: 'build-host',
    kind: 'external',
    component: 'mohist-runner',
    sourceRevision: 'source-abc',
    releaseId: 'release-42',
    generation: 7,
  },
  presence: { state: 'online', lastObservedAt: '2026-01-01T12:00:00Z' },
  control: { state: 'connected', generation: 'server-epoch:12' },
  admission: { state: 'blocked', reasonCodes: ['draining', 'capacity-full'] },
  capabilities: ['spec/*', 'workspace-query'],
  runtimes: [
    {
      name: 'pi',
      readiness: { state: 'not-ready', generation: null, reasonCode: 'runtime-witness-missing' },
      catalog: {
        complete: true,
        capabilityRevision: 'catalog-sha',
        modelCount: 2,
        models: ['openai/gpt-5', 'anthropic/claude-sonnet'],
        variants: { 'openai/gpt-5': ['high', 'medium'] },
        supportsReasoningEffort: true,
        reasoningEfforts: { 'openai/gpt-5': ['high'] },
      },
    },
  ],
  capacity: { used: 2, total: 2 },
  activeWorks: [
    {
      workId: 'workflow-work',
      ownerKind: 'workflow',
      ownerId: 'workflow-123',
      workType: 'workflow',
      stage: 'Build',
      title: 'Workflow work',
      issue: { projectId: 'project-not-loaded', issueNumber: 12 },
    },
    {
      workId: 'agent-job-work',
      ownerKind: 'agent-job',
      ownerId: 'agent-job-456',
      workType: 'agent-job',
      stage: null,
      title: 'AgentJob work',
      issue: null,
    },
  ],
  drain: { active: true, kind: 'update', updateInterruptId: 'interrupt-42' },
  nextActions: [
    { code: 'wait-for-capacity', message: 'Wait for the active owner to release a Runner slot.', command: null },
    { code: 'wait-for-runtime', message: 'Wait for the Runtime to report ready.', command: null },
  ],
}

async function mockRunnerApi(page: Page, runners: unknown[] = [offlineRunner, liveRunner]) {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    if (route.request().method() !== 'GET')
      return route.fulfill({ status: 404, json: { success: false, error: 'Unhandled method' } })
    if (path === '/auth/session') return route.fulfill({ json: response(null) })
    if (path === '/projects') return route.fulfill({ json: response([]) })
    if (path === '/runners') {
      return route.fulfill({
        json: response({
          observedAt: '2026-01-01T12:00:00Z',
          inventory: {
            state: runners.length === 0 ? 'first-install' : 'ready',
            nextActions:
              runners.length === 0
                ? [
                    {
                      code: 'install-runner',
                      message: 'Install and start the first Runner.',
                      command: 'mo install runner --repo-root <path>',
                    },
                  ]
                : [],
          },
          runners,
        }),
      })
    }
    if (path === `/runners/${encodeURIComponent(runnerId)}`)
      return route.fulfill({ json: response({ observedAt: '2026-01-01T12:00:00Z', runner: offlineRunner }) })
    if (path === '/runners/runner-draining-with-mixed-owner-work')
      return route.fulfill({ json: response({ observedAt: '2026-01-01T12:00:00Z', runner: liveRunner }) })
    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled route: ${path}` } })
  })
}

async function expectNoHorizontalOverflow(page: Page) {
  const metrics = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }))
  expect(metrics.scrollWidth).toBeLessThanOrEqual(metrics.clientWidth)
}

test('Runner inventory is global and works with no Projects', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await mockRunnerApi(page)
  await page.goto('/runners')

  await expect(page.getByTestId('runners-page')).toBeVisible()
  await expect(page.getByTestId('nav-runners')).toBeVisible()
  await expect(page.getByText(runnerId)).toBeVisible()
  await expect(page.getByText('offline', { exact: true })).toBeVisible()
  await expect(page.getByText('control disconnected')).toBeVisible()
  await expect(page.getByText('admission blocked')).toHaveCount(2)
  await expect(page.getByText('mo install runner --repo-root <path> --runner-id ' + runnerId)).toBeVisible()
  await expect(page.getByText(/No projects yet/)).toHaveCount(0)
  await expectNoHorizontalOverflow(page)
})

test('Runner detail keeps Runtime facts separate and fits mixed owners on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockRunnerApi(page)
  await page.goto('/runners/runner-draining-with-mixed-owner-work')

  await expect(page.getByTestId('runner-detail-page')).toBeVisible()
  await expect(page.getByTestId('runner-detail-component')).toHaveText('mohist-runner')
  await expect(page.getByTestId('runner-detail-source-revision')).toHaveText('source-abc')
  await expect(page.getByTestId('runner-detail-release-id')).toHaveText('release-42')
  await expect(page.getByTestId('runner-runtime-readiness')).toHaveText('not-ready')
  await expect(page.getByTestId('runner-runtime-models')).toContainText('openai/gpt-5')
  await expect(page.getByTestId('runner-runtime-variants')).toContainText('high')
  await expect(page.getByTestId('runner-runtime-reasoning-support')).toHaveText('supported')
  await expect(page.getByTestId('runner-drain')).toContainText('interrupt-42')
  await expect(page.getByText('Workflow', { exact: true })).toBeVisible()
  await expect(page.getByText('AgentJob', { exact: true })).toBeVisible()
  await expect(page.getByText('project-not-loaded · #12')).toBeVisible()
  await expect(page.getByText(/idle|busy/i)).toHaveCount(0)
  await expectNoHorizontalOverflow(page)
})

test('Runner inventory exposes only the Server first-install action', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockRunnerApi(page, [])
  await page.goto('/runners')

  await expect(page.getByText('No Runner definitions')).toBeVisible()
  await expect(page.getByText('mo install runner --repo-root <path>')).toBeVisible()
  await expect(page.getByText('mo service start runner')).toHaveCount(0)
  await expectNoHorizontalOverflow(page)
})
