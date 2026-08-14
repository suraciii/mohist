import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-workflow-task-inspector',
  name: 'workflow-task-inspector',
  repositories: [],
  createdAt: '2026-07-01T00:00:00Z',
  updatedAt: '2026-07-01T00:00:00Z',
}

const issueNumber = 454
const workflowRunId = 'wr-task-inspector'
const longTitle = 'Complete the responsive workflow inspector while preserving this intentionally long task title on a phone viewport'
const stageTasks = {
  plan: 'Inspect the plan task',
  build: longTitle,
  check: 'Inspect the check task',
  integrate: 'Inspect the pending integrate task',
} as const

function apiResponse(data: unknown) {
  return { success: true, data }
}

function makeIssue() {
  return {
    number: issueNumber,
    title: 'Responsive workflow task inspector',
    body: 'Validate the canonical workflow task presentation.',
    status: 'done',
    workflowStage: 'build',
    workflowStatus: 'completed',
    workflowRunId,
    workflowProfileId: 'mohist/github-pr',
    health: 'done',
    projectId: project.id,
    labels: {},
    createdAt: '2026-07-01T00:00:00Z',
    updatedAt: '2026-07-01T00:04:00Z',
    comments: [],
    attachments: [],
    priority: 'p2',
    prerequisites: [],
    isDraft: false,
    canStart: false,
    blocker: null,
    feedback: [],
  }
}

function makeTimeline() {
  const stages = ['plan', 'build', 'check', 'integrate'] as const
  return {
    workflowRunId,
    status: 'Completed',
    currentStage: 'build',
    pendingWork: null,
    stages: stages.map((stage, index) => {
      const pending = stage === 'integrate'
      const completedAt = pending ? null : `2026-07-01T00:0${index + 1}:00Z`
      return {
        stage,
        status: pending ? 'pending' : 'completed',
        order: index,
        startedAt: pending ? null : `2026-07-01T00:0${index}:00Z`,
        completedAt,
        durationMs: pending ? null : 60000,
        tasks: [{
          id: `${stage}-task`,
          title: stageTasks[stage],
          uses: stage === 'build' ? 'mohist/coder-agent-with-a-long-runtime-origin' : 'core/script',
          sessionName: stage === 'build' ? 'build-session-with-a-long-name' : null,
          status: pending ? 'pending' : 'completed',
          startedAt: pending ? null : `2026-07-01T00:0${index}:00Z`,
          completedAt,
          durationMs: pending ? null : 60000,
          attempts: stage === 'build' ? 2 : 1,
          message: null,
          output: stage === 'build' ? { result: 'complete' } : null,
          artifactSummaries: stage === 'build' ? [{
            artifactId: 'artifact-long',
            path: 'openspec/changes/issue-454/a-very-long-artifact-summary-path-for-phone-layout.md',
            kind: 'file',
            size: 128,
            recordedAt: completedAt,
          }] : [],
        }],
        checks: [],
        approval: null,
      }
    }),
    availableActions: [],
  }
}

async function mockApi(page: Page) {
  await page.route('**/hubs/events**', route => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()

    if (method === 'GET' && path === '/projects') return route.fulfill({ json: apiResponse([project]) })
    if (method === 'GET' && path === `/projects/${project.id}/agent/status`) {
      return route.fulfill({ json: apiResponse({ running: false, runnerAvailable: true, activeAgents: [], capacity: { active: 0, max: 4 } }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}`) return route.fulfill({ json: apiResponse(makeIssue()) })
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/workflow/status`) return route.fulfill({ json: apiResponse({ workflow: makeTimeline() }) })
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}/workflow/tasks/build-task/logs`) {
      return route.fulfill({ json: apiResponse({ lines: [{ seq: 1, timestamp: '2026-07-01T00:02:30Z', source: 'action:build', text: 'Browser-visible canonical task log' }], nextCursor: null, truncated: false }) })
    }
    if (method === 'GET' && path.includes('/workflow/tasks/') && path.endsWith('/logs')) {
      return route.fulfill({ json: apiResponse({ lines: [], nextCursor: null, truncated: false }) })
    }
    if (method === 'GET' && path === `/workflow-runs/${workflowRunId}/sessions`) return route.fulfill({ json: apiResponse([]) })
    if (method === 'GET' && path.endsWith('/diff')) return route.fulfill({ json: apiResponse({ available: false, message: 'No diff fixture' }) })
    if (method === 'GET' && path.endsWith('/commits')) return route.fulfill({ json: apiResponse({ available: false, message: 'No commits fixture' }) })
    if (method === 'GET' && path.endsWith('/workflow/artifacts')) return route.fulfill({ json: apiResponse([]) })
    if (method === 'GET' && path.endsWith('/coder-sessions')) return route.fulfill({ json: apiResponse([]) })
    if (method === 'GET' && path.endsWith('/workflow-profile')) return route.fulfill({ json: apiResponse(null) })
    if (method === 'GET' && path.endsWith('/variables')) return route.fulfill({ json: apiResponse({ vars: {}, stages: {} }) })
    if (method === 'GET' && path.endsWith('/workspace-status')) return route.fulfill({ json: apiResponse(null) })
    if (method === 'GET' && path.endsWith('/opencode/models')) return route.fulfill({ json: apiResponse({ models: [], modelVariants: {} }) })
    if (method === 'GET' && path === `/projects/${project.id}/issues`) return route.fulfill({ json: apiResponse([]) })
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profile/default`) {
      return route.fulfill({ json: apiResponse({ projectId: project.id, defaultWorkflowProfileId: 'mohist/github-pr', disabledWorkflowProfileIds: [] }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profiles`) return route.fulfill({ json: apiResponse([]) })

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled browser fixture: ${method} ${path}` } })
  })
}

test('phone workflow inspector exposes one responsive task list with honest disclosures', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockApi(page)
  await page.goto(`/${project.name}/issues/${issueNumber}`)

  const stageBar = page.getByTestId('workflow-stage-bar')
  await expect(stageBar).toBeVisible()
  const stageButtons = stageBar.getByRole('button')
  await expect(stageButtons).toHaveCount(4)

  const stageBoxes = await stageButtons.evaluateAll(buttons => buttons.map(button => {
    const box = button.getBoundingClientRect()
    return { left: Math.round(box.left), top: Math.round(box.top), right: Math.round(box.right) }
  }))
  expect(new Set(stageBoxes.map(box => box.top)).size).toBe(2)
  expect(new Set(stageBoxes.map(box => box.left)).size).toBe(2)
  expect(Math.max(...stageBoxes.map(box => box.right))).toBeLessThanOrEqual(390)

  for (const stage of ['Plan', 'Build', 'Check', 'Integrate'] as const) {
    const stageButton = stageBar.getByRole('button', { name: new RegExp(`^${stage}`) })
    await stageButton.click()
    await expect(stageButton).toHaveAttribute('aria-current', 'step')
    await expect(page.getByText(stageTasks[stage.toLowerCase() as keyof typeof stageTasks], { exact: true })).toBeVisible()
    await expect(page.getByTestId('workflow-task-item')).toHaveCount(1)
  }

  await stageBar.getByRole('button', { name: /^Plan/ }).click()
  expect(await page.getByTestId('workflow-task-item').locator('button').count()).toBe(0)

  await stageBar.getByRole('button', { name: /^Build/ }).click()
  const row = page.getByTestId('workflow-task-item')
  const title = row.getByText(longTitle, { exact: true })
  const metadata = row.getByTestId('workflow-task-metadata')
  const disclosure = row.getByRole('button', { name: longTitle })
  await expect(title).toBeVisible()
  await expect(metadata).toContainText('a-very-long-artifact-summary-path-for-phone-layout.md')
  await expect(metadata).toContainText('runtime:coder-agent-with-a-long-runtime-origin')
  await expect(metadata).toContainText('build-session-with-a-long-name')
  await expect(disclosure).toHaveAttribute('aria-expanded', 'false')
  expect(await disclosure.locator('button, a').count()).toBe(0)

  const [titleBox, metadataBox] = await Promise.all([title.boundingBox(), metadata.boundingBox()])
  expect(titleBox).not.toBeNull()
  expect(metadataBox).not.toBeNull()
  expect(titleBox!.y + titleBox!.height).toBeLessThanOrEqual(metadataBox!.y)

  await disclosure.click()
  await expect(disclosure).toHaveAttribute('aria-expanded', 'true')
  await expect(row.getByText('Browser-visible canonical task log')).toBeVisible()
  await disclosure.click()
  await expect(row.getByTestId('workflow-task-details')).toHaveCount(0)

  await stageBar.getByRole('button', { name: /^Integrate/ }).click()
  const pendingRow = page.getByTestId('workflow-task-item')
  await expect(pendingRow.getByText(stageTasks.integrate)).toBeVisible()
  expect(await pendingRow.locator('button').count()).toBe(0)

  const overflow = await page.evaluate(() => ({
    document: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    stageBar: document.querySelector('[data-testid="workflow-stage-bar"]')!.scrollWidth
      - document.querySelector('[data-testid="workflow-stage-bar"]')!.clientWidth,
  }))
  expect(overflow.document).toBeLessThanOrEqual(0)
  expect(overflow.stageBar).toBeLessThanOrEqual(0)
})
