import { expect, test, type Locator, type Page } from '@playwright/test'

const project = {
  id: 'proj-fragment-browser',
  name: 'fragment-browser',
  repositories: [],
  createdAt: '2026-07-21T00:00:00Z',
  updatedAt: '2026-07-21T00:00:00Z',
}

const ordinaryIssueNumber = 501
const approvalIssueNumber = 502

function response(data: unknown) {
  return { success: true, data }
}

function makeIssue(number: number, approval: boolean) {
  return {
    number,
    title: approval ? 'Approval fragment destination' : 'Ordinary fragment destinations',
    body: '# Description\n\n' + 'Detailed issue context keeps the comments destination below the workflow content. '.repeat(25),
    status: approval ? 'in_progress' : 'backlog',
    workflowStage: approval ? 'check' : null,
    workflowStatus: approval ? 'paused' : null,
    workflowRunId: approval ? 'wr-fragment-approval' : null,
    health: approval ? 'paused' : 'active',
    approvalState: approval
      ? { status: 'awaiting', stage: 'check', requestedAt: '2026-07-21T01:00:00Z' }
      : null,
    recovery: approval
      ? {
          currentWorkItem: null,
          latestAttemptState: null,
          workflowSummaryState: 'awaiting-approval',
          allowedActions: ['approve', 'reject'],
        }
      : null,
    projectId: project.id,
    projectName: project.name,
    labels: {},
    priority: 'p2',
    risk: null,
    prerequisites: [],
    attachments: [],
    comments: [{
      id: `comment-${number}`,
      author: 'Fragment reviewer',
      body: 'The comments destination is visible.',
      createdAt: '2026-07-21T02:00:00Z',
      attachments: [],
    }],
    children: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    createdAt: '2026-07-21T00:00:00Z',
    updatedAt: '2026-07-21T02:00:00Z',
  }
}

async function mockApi(page: Page) {
  const issues = new Map([
    [ordinaryIssueNumber, makeIssue(ordinaryIssueNumber, false)],
    [approvalIssueNumber, makeIssue(approvalIssueNumber, true)],
  ])

  await page.route('**/hubs/events**', route => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()
    const issueMatch = path.match(new RegExp(`^/projects/${project.id}/issues/(\\d+)`))
    const issueNumber = issueMatch ? Number(issueMatch[1]) : null
    const issue = issueNumber === null ? null : issues.get(issueNumber)

    if (method === 'GET' && path === '/projects') return route.fulfill({ json: response([project]) })
    if (method === 'GET' && path === `/projects/${project.id}/issues`) return route.fulfill({ json: response([]) })
    if (method === 'GET' && issue && path === `/projects/${project.id}/issues/${issueNumber}`) {
      return route.fulfill({ json: response(issue) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/agent/status`) {
      return route.fulfill({ json: response({ running: false, runnerAvailable: true, activeAgents: [], capacity: { active: 0, max: 4 } }) })
    }
    if (method === 'GET' && issue && path.endsWith('/workflow/status')) {
      return route.fulfill({
        json: response({
          workflow: issueNumber === approvalIssueNumber
            ? {
                workflowRunId: 'wr-fragment-approval',
                status: 'paused',
                currentStage: 'check',
                pendingWork: null,
                stages: [],
                availableActions: ['approve', 'reject'],
              }
            : null,
        }),
      })
    }
    if (method === 'GET' && issue && path.endsWith('/workflow/artifacts')) {
      return route.fulfill({ json: response([]) })
    }
    if (method === 'GET' && issue && path.endsWith('/events')) return route.fulfill({ json: response([]) })
    if (method === 'GET' && issue && path.endsWith('/diff')) {
      return route.fulfill({ json: response({ available: false, reason: 'not_started', message: 'No comparison is available.' }) })
    }
    if (method === 'GET' && issue && path.endsWith('/commits')) {
      return route.fulfill({ json: response({ available: false, reason: 'not_started', message: 'No comparison is available.' }) })
    }
    if (method === 'GET' && issue && path.endsWith('/workspace-status')) {
      return route.fulfill({ json: response({ exists: false, reason: 'not_started' }) })
    }
    if (method === 'GET' && issue && path.endsWith('/workflow-profile/variables')) {
      return route.fulfill({ json: response({ vars: {}, stages: {} }) })
    }
    if (method === 'GET' && issue && path.endsWith('/workflow-profile')) {
      return route.fulfill({ json: response({ issueNumber, projectId: project.id, hasCustomTemplate: false, yaml: null, workflowRunId: null, profileId: '' }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profile/variables`) {
      return route.fulfill({ json: response({ vars: {}, stages: {} }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profile`) {
      return route.fulfill({ json: response({ projectId: project.id, defaultTemplateId: null, disabledWorkflowProfileIds: [] }) })
    }
    if (method === 'GET' && path.endsWith('/opencode/models')) {
      return route.fulfill({ json: response({ models: [], modelVariants: {} }) })
    }
    if (method === 'GET' && path === '/workflow-templates/system') return route.fulfill({ json: response([]) })
    if (method === 'GET' && path === '/workflow-runs/wr-fragment-approval/sessions') return route.fulfill({ json: response([]) })
    if (method === 'GET' && path === '/workflow-runs/wr-fragment-approval/yaml') {
      return route.fulfill({ json: response({ workflowRunId: 'wr-fragment-approval', yaml: '' }) })
    }

    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled fragment fixture: ${method} ${path}` } })
  })
}

async function expectNoHorizontalOverflow(page: Page) {
  const overflow = await page.evaluate(() => ({
    document: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    page: (() => {
      const element = document.querySelector('[data-testid="issue-detail-page-container"]')
      return element ? element.scrollWidth - element.clientWidth : 0
    })(),
  }))
  expect(overflow.document).toBeLessThanOrEqual(0)
  expect(overflow.page).toBeLessThanOrEqual(0)
}

async function expectSectionVisible(page: Page, target: Locator) {
  await expect(target).toBeVisible()
  await expect(target).toBeInViewport()

  const targetBox = await target.boundingBox()
  const stickyBox = await page.getByTestId('status-headline').boundingBox()
  expect(targetBox).not.toBeNull()
  expect(targetBox!.x).toBeGreaterThanOrEqual(0)
  expect(targetBox!.x + targetBox!.width).toBeLessThanOrEqual(page.viewportSize()!.width)
  if (stickyBox && targetBox!.y < stickyBox.y + stickyBox.height) {
    throw new Error('Fragment target is obscured by the sticky status headline')
  }

  const centerIsTarget = await target.evaluate((element) => {
    const box = element.getBoundingClientRect()
    const hit = document.elementFromPoint(box.left + Math.min(box.width / 2, 20), box.top + Math.min(box.height / 2, 20))
    return hit === element || (hit !== null && element.contains(hit))
  })
  expect(centerIsTarget).toBe(true)
  await expectNoHorizontalOverflow(page)
}

const viewports = [
  { name: 'desktop', width: 1280, height: 900 },
  { name: 'phone', width: 390, height: 844 },
] as const

for (const viewport of viewports) {
  test(`${viewport.name} issue fragments reveal final destinations without overlap or overflow`, async ({ page }) => {
    await page.setViewportSize({ width: viewport.width, height: viewport.height })
    await mockApi(page)

    const ordinaryCases = [
      { fragment: 'workflow', target: () => page.locator('#workflow') },
      { fragment: 'artifacts', target: () => page.getByTestId('latest-artifacts-panel') },
      { fragment: 'comments', target: () => page.getByTestId('comments-section') },
    ]

    for (const scenario of ordinaryCases) {
      await page.goto(`/${project.name}/issues/${ordinaryIssueNumber}?scope=kept#${scenario.fragment}`)
      await expectSectionVisible(page, scenario.target())
      await expect(page).toHaveURL(new RegExp(`\\?scope=kept#${scenario.fragment}$`))
    }

    await page.goto(`/${project.name}/issues/${approvalIssueNumber}?scope=kept#artifacts`)
    const approvalEvidence = page.getByTestId('approval-review-evidence')
    await expectSectionVisible(page, approvalEvidence)
    await expect(page.locator('#artifacts')).toHaveCount(1)
    await expect(page.getByTestId('latest-artifacts-panel')).toHaveCount(0)

    await page.goto(`/${project.name}/issues/${ordinaryIssueNumber}?scope=kept#activity`)
    const dialog = page.getByTestId('activity-dialog-content')
    await expect(dialog).toBeVisible()
    await expect(dialog).toHaveAttribute('id', 'activity')
    await expect(page.getByTestId('event-timeline-panel')).toBeVisible()
    await expect(page).toHaveURL(/\?scope=kept#activity$/)
    await expectNoHorizontalOverflow(page)
  })
}
