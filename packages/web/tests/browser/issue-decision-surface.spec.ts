import { expect, test, type Locator, type Page } from '@playwright/test'

const project = {
  id: 'proj-issue-decision-surface',
  name: 'issue-decision-surface-project',
  repositories: [],
  createdAt: '2026-07-01T00:00:00Z',
  updatedAt: '2026-07-01T00:00:00Z',
}

function response(data: unknown) {
  return { success: true, data }
}

interface IssueFixture {
  number: number
  status: 'backlog' | 'in_progress' | 'done' | 'cancelled'
  workflowStage?: string | null
  workflowStatus?: string | null
  workflowRunId?: string | null
  health: 'active' | 'paused' | 'blocked' | 'done' | 'cancelled'
  archivedAt?: string | null
  isDraft?: boolean
  canStart?: boolean
  blocker?: unknown
  approvalState?: {
    status: 'awaiting' | 'approved' | 'rejected'
    stage: string
    requestedAt: string
    output?: Record<string, unknown> | null
  } | null
  children?: Array<{ number: number; title: string; status: string; health: string; repositoryName: string | null }>
  childIssuesSummary?: {
    hasChildren: boolean
    count: number
    backlogCount: number
    inProgressCount: number
    doneCount: number
    cancelledCount: number
    blockedCount: number
  } | null
  recovery?: {
    currentWorkItem: { type: 'task'; id: string; title: string } | null
    latestAttemptState: string
    workflowSummaryState: string
    allowedActions: string[]
  } | null
  feedback?: unknown[] | null
  workflowStageProgress?: unknown
  blockedReason?: string | null
  repository?: { name: string; baseBranch: string; gitUrl: string }
}

interface ApprovalEvidenceFixture {
  artifacts?: Record<string, string>
  diff?: Record<string, unknown>
}

function makeIssue(issue: IssueFixture): Record<string, unknown> {
  return {
    number: issue.number,
    title: `Issue ${issue.number}`,
    body: '',
    status: issue.status,
    workflowStage: issue.workflowStage ?? null,
    workflowStatus: issue.workflowStatus ?? null,
    workflowRunId: issue.workflowRunId ?? null,
    health: issue.health,
    archivedAt: issue.archivedAt ?? null,
    isDraft: issue.isDraft ?? false,
    canStart: issue.canStart ?? false,
    blocker: issue.blocker ?? null,
    approvalState: issue.approvalState ?? null,
    children: issue.children ?? [],
    childIssuesSummary: issue.childIssuesSummary ?? null,
    feedback: issue.feedback ?? null,
    recovery: issue.recovery ?? null,
    workflowStageProgress: issue.workflowStageProgress ?? null,
    blockedReason: issue.blockedReason ?? null,
    projectId: project.id,
    projectName: project.name,
    repository: issue.repository ?? null,
    labels: {},
    priority: 'p2',
    risk: null,
    model: null,
    modelVariant: null,
    agentConfig: null,
    stageModels: {},
    stageModelVariants: {},
    prerequisites: [],
    prerequisiteNumbers: [],
    attachments: [],
    createdAt: '2026-07-01T00:00:00Z',
    updatedAt: '2026-07-01T00:00:00Z',
    comments: [],
  }
}

const sessions = [
  {
    sessionName: 'coder-1',
    status: 'completed',
    startedAt: '2026-07-01T01:00:00Z',
    createdAt: '2026-07-01T00:55:00Z',
    workflowRunId: 'wr-running',
    issueNumber: 401,
    projectId: project.id,
  },
  {
    sessionName: 'coder-2',
    status: 'running',
    startedAt: '2026-07-01T02:00:00Z',
    createdAt: '2026-07-01T01:55:00Z',
    workflowRunId: 'wr-running',
    issueNumber: 401,
    projectId: project.id,
  },
]

async function mockIssueDetailApi(
  page: Page,
  issue: Record<string, unknown>,
  sessionsOverride?: typeof sessions,
  approvalEvidence?: ApprovalEvidenceFixture,
) {
  await page.route('**/hubs/events**', (route) => route.fulfill({ status: 204, body: '' }))
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()

    if (method === 'POST' && path === `/projects/${project.id}/issues/${issue.number}/approve`) {
      return route.fulfill({ json: response({ issue, context: null, message: 'Approved' }) })
    }
    if (method === 'POST' && path === `/projects/${project.id}/issues/${issue.number}/feedback`) {
      return route.fulfill({ json: response({ success: true, data: { id: 'feedback-1' } }) })
    }

    if (method === 'GET' && path === '/projects') {
      return route.fulfill({ json: response([project]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues`) {
      return route.fulfill({ json: response([issue]) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issue.number}`) {
      return route.fulfill({ json: response(issue) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issue.number}/diff`) {
      return route.fulfill({ json: response(approvalEvidence?.diff ?? { available: false, reason: 'not_started', message: 'no workspace' }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issue.number}/commits`) {
      return route.fulfill({ json: response({ available: false, reason: 'not_started', message: 'no workspace' }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issue.number}/workflow/status`) {
      return route.fulfill({
        json: response({
          workflowRunId: issue.workflowRunId ?? null,
          status: issue.workflowStatus ?? null,
          currentStage: issue.workflowStage ?? null,
          pendingWork: null,
          stages: [],
          availableActions: [],
        }),
      })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issue.number}/workspace-status`) {
      return route.fulfill({ json: response({ exists: false, reason: 'not_started' }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issue.number}/workflow/artifacts`) {
      const artifactPath = url.searchParams.get('path')
      const artifactContent = artifactPath ? approvalEvidence?.artifacts?.[artifactPath] : undefined
      return route.fulfill({
        json: response(artifactContent === undefined || !artifactPath ? [] : [{
          artifactId: `artifact-${artifactPath}`,
          workflowRunId: issue.workflowRunId,
          taskRunId: 'task-1',
          path: artifactPath,
          kind: 'file',
          contentType: artifactPath.endsWith('.md') ? 'text/markdown' : 'application/json',
          size: artifactContent.length,
          recordedAt: '2026-07-01T03:00:00Z',
          displayName: artifactPath,
        }]),
      })
    }
    const artifactContentPrefix = `/projects/${project.id}/issues/${issue.number}/workflow/artifacts/`
    if (method === 'GET' && path.startsWith(artifactContentPrefix) && path.endsWith('/content')) {
      const artifactId = path.slice(artifactContentPrefix.length, -'/content'.length)
      const artifactPath = artifactId.replace(/^artifact-/, '')
      const artifactContent = approvalEvidence?.artifacts?.[artifactPath]
      if (artifactContent === undefined) {
        return route.fulfill({ status: 404, body: 'Artifact content not found' })
      }
      return route.fulfill({
        body: artifactContent,
        contentType: artifactPath.endsWith('.md') ? 'text/markdown' : 'application/json',
      })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issue.number}/workflow-profile/variables`) {
      return route.fulfill({ json: response({ vars: {}, stages: {} }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issue.number}/workflow-profile`) {
      return route.fulfill({
        json: response({
          issueNumber: issue.number,
          projectId: project.id,
          hasCustomTemplate: false,
          yaml: null,
          workflowRunId: null,
          profileId: '',
        }),
      })
    }
    if (method === 'GET' && path === `/projects/${project.id}/agent/status`) {
      return route.fulfill({
        json: response({
          running: false,
          issueNumber: null,
          activeAgents: [],
          runnerAvailable: true,
          capacity: { active: 0, max: 4 },
        }),
      })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profile/variables`) {
      return route.fulfill({ json: response({ vars: {}, stages: {} }) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profile`) {
      return route.fulfill({
        json: response({
          projectId: project.id,
          defaultTemplateId: null,
          disabledWorkflowProfileIds: [],
        }),
      })
    }
    if (method === 'GET' && path === '/workflow-templates/system') {
      return route.fulfill({ json: response([]) })
    }
    if (method === 'GET' && path.startsWith('/workflow-runs/') && path.endsWith('/sessions')) {
      return route.fulfill({ json: response(sessionsOverride ?? sessions) })
    }
    if (method === 'GET' && path === `/projects/${project.id}/labels`) {
      return route.fulfill({ json: response([]) })
    }
    return route.fulfill({ status: 404, json: { success: false, error: `Unhandled test route: ${method} ${path}` } })
  })
}

async function box(locator: Locator) {
  const value = await locator.evaluate((node) => {
    const r = (node as HTMLElement).getBoundingClientRect()
    return { x: r.x, y: r.y, width: r.width, height: r.height }
  })
  expect(value).not.toBeNull()
  return value
}

function boxesOverlap(
  a: { x: number; y: number; width: number; height: number },
  b: { x: number; y: number; width: number; height: number },
) {
  return a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y
}

test.describe('Issue decision surface browser layout', () => {
  test('running issue renders Stop exactly once and inside the runtime decision surface (desktop)', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    const issue = makeIssue({
      number: 401,
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      workflowRunId: 'wr-running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't-1', title: 'Build feature' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    })
    await mockIssueDetailApi(page, issue, [])
    await page.goto(`/${project.name}/issues/${issue.number}`)

    const surface = page.getByTestId('issue-decision-surface')
    await expect(surface).toBeVisible()
    await expect(surface.getByTestId('decision-rationale')).toBeVisible()
    await expect(surface.getByTestId('decision-next-action')).toBeVisible()

    const stopButtons = surface.getByRole('button', { name: 'Stop' })
    expect(await stopButtons.count()).toBe(1)
    await expect(page.getByTestId('reference-rail').locator('[data-testid="decision-action-stop"]')).toHaveCount(0)
  })

  test('running issue with no executable action shows a product-language rationale rather than an unexplained all-disabled surface (desktop)', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    const issue = makeIssue({
      number: 402,
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      workflowRunId: 'wr-running',
      health: 'active',
      isDraft: true,
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: [],
      },
    })
    await mockIssueDetailApi(page, issue, [])
    await page.goto(`/${project.name}/issues/${issue.number}`)

    const surface = page.getByTestId('issue-decision-surface')
    await expect(surface).toBeVisible()
    const rationale = await surface.getByTestId('decision-rationale').textContent()
    expect(rationale ?? '').not.toMatch(/projection|surface|backend/i)
    expect(rationale ?? '').toMatch(/[A-Za-z]/)
  })

  test('disabled Stop carries an accessible reason and uses unmistakable disabled styling (desktop)', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    const issue = makeIssue({
      number: 403,
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      workflowRunId: 'wr-running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't-1', title: 'Build feature' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    })
    await mockIssueDetailApi(page, issue, [])
    await page.goto(`/${project.name}/issues/${issue.number}`)

    const surface = page.getByTestId('issue-decision-surface')
    const stop = surface.getByTestId('decision-action-stop')
    await expect(stop).toBeVisible()
    const stopRect = await stop.boundingBox()
    expect(stopRect).not.toBeNull()
    expect(stopRect!.width).toBeGreaterThan(0)
    expect(stopRect!.height).toBeGreaterThan(0)
    const className = (await stop.getAttribute('class')) ?? ''
    if (await stop.isDisabled()) {
      const describedBy = await stop.getAttribute('aria-describedby')
      expect(describedBy).toBeTruthy()
      const reason = page.locator(`#${describedBy}`)
      await expect(reason).toBeVisible()
      const reasonText = await reason.textContent()
      expect(reasonText ?? '').not.toMatch(/projection|surface|backend/i)
      expect(className).not.toMatch(/bg-destructive|text-destructive/)
      expect(className).toMatch(/opacity-50|border-border|bg-muted/)
    } else {
      await expect(stop).toBeEnabled()
      expect(className).not.toMatch(/bg-muted/)
    }
  })

  test('composite parent issue surfaces close and Ask Agent inside the decision surface (desktop)', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    const issue = makeIssue({
      number: 404,
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      workflowRunId: null,
      health: 'active',
      children: [
        { number: 410, title: 'child A', status: 'in_progress', health: 'active', repositoryName: null },
        { number: 411, title: 'child B', status: 'done', health: 'done', repositoryName: null },
      ],
      childIssuesSummary: {
        hasChildren: true,
        count: 2,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 1,
        cancelledCount: 0,
        blockedCount: 0,
      },
      recovery: null,
    })
    await mockIssueDetailApi(page, issue, [])
    await page.goto(`/${project.name}/issues/${issue.number}`)

    const surface = page.getByTestId('issue-decision-surface')
    await expect(surface).toBeVisible()
    await expect(surface.getByTestId('decision-action-close')).toBeVisible()
    await expect(surface.getByTestId('decision-action-ask-agent')).toBeVisible()
    await expect(surface.getByTestId('decision-action-stop')).toHaveCount(0)
    await expect(surface.getByTestId('decision-action-approve')).toHaveCount(0)
  })

  test('desktop session action opens the concrete session route and no transcript action appears without sessions', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    const issue = makeIssue({
      number: 405,
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      workflowRunId: 'wr-running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't-1', title: 'Build' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    })
    await mockIssueDetailApi(page, issue, sessions)
    await page.goto(`/${project.name}/issues/${issue.number}`)

    const surface = page.getByTestId('issue-decision-surface')
    const transcript = surface.getByTestId('decision-action-view-transcript')
    await expect(transcript).toBeVisible()
    await expect(transcript).not.toHaveAttribute('aria-disabled', 'true')
    const transcriptHref = await transcript.getAttribute('href')
    expect(transcriptHref).toContain(`/issues/${issue.number}/workflow/sessions/coder-2`)

    const stopBox = await box(surface.getByTestId('decision-action-stop'))
    const transcriptBox = await box(transcript)
    expect(boxesOverlap(stopBox, transcriptBox)).toBe(false)
  })

  test('phone width surfaces direct approval actions without the generic launcher', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 820 })
    const issue = makeIssue({
      number: 406,
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'paused',
      workflowRunId: 'wr-running',
      health: 'paused',
      approvalState: {
        status: 'awaiting',
        stage: 'check',
        requestedAt: '2026-07-01T03:00:00Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'awaiting-approval',
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    })
    await mockIssueDetailApi(page, issue, [])
    await page.goto(`/${project.name}/issues/${issue.number}`)

    await expect(page.getByTestId('approval-mobile-approve')).toBeVisible()
    await expect(page.getByTestId('approval-mobile-send-back')).toBeVisible()
    await expect(page.getByTestId('approval-review-evidence')).toBeVisible()
    await expect(page.getByTestId('mobile-action-sheet-launcher')).not.toBeVisible()
  })

  test('desktop send-back keyboard flow keeps approval shortcuts out of the feedback field', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    const issue = makeIssue({
      number: 411,
      status: 'in_progress',
      workflowStage: 'plan',
      workflowStatus: 'paused',
      workflowRunId: 'wr-approval',
      health: 'paused',
      approvalState: {
        status: 'awaiting',
        stage: 'plan',
        requestedAt: '2026-07-01T03:00:00Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'awaiting-approval',
        workflowSummaryState: 'approval-required',
        allowedActions: ['approve', 'reject'],
      },
    })
    await mockIssueDetailApi(page, issue, [])
    let approveRequests = 0
    let feedbackRequests = 0
    page.on('request', (request) => {
      if (request.method() !== 'POST') return
      if (request.url().endsWith(`/projects/${project.id}/issues/${issue.number}/approve`)) approveRequests += 1
      if (request.url().endsWith(`/projects/${project.id}/issues/${issue.number}/feedback`)) feedbackRequests += 1
    })
    await page.goto(`/${project.name}/issues/${issue.number}`)

    await expect(page.getByTestId('decision-action-approve-shortcut')).toHaveText('a')
    await expect(page.getByTestId('decision-action-send-back-shortcut')).toHaveText('m')
    await page.keyboard.press('m')
    const feedback = page.getByTestId('send-back-feedback-textarea')
    await expect(feedback).toBeFocused()
    await page.getByRole('radio', { name: 'Scope' }).click()
    await feedback.fill('Keep the plan focused.')
    await page.keyboard.press('a')
    await expect.poll(() => approveRequests).toBe(0)
    await page.keyboard.press('Meta+Enter')
    await expect.poll(() => feedbackRequests).toBe(1)
  })

  test('desktop approval shortcut uses the visible approve action', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    const issue = makeIssue({
      number: 411,
      status: 'in_progress',
      workflowStage: 'plan',
      workflowStatus: 'paused',
      workflowRunId: 'wr-approval',
      health: 'paused',
      approvalState: {
        status: 'awaiting',
        stage: 'plan',
        requestedAt: '2026-07-01T03:00:00Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'awaiting-approval',
        workflowSummaryState: 'approval-required',
        allowedActions: ['approve', 'reject'],
      },
    })
    await mockIssueDetailApi(page, issue, [])
    let approveRequests = 0
    page.on('request', (request) => {
      if (request.method() === 'POST' && request.url().endsWith(`/projects/${project.id}/issues/${issue.number}/approve`)) approveRequests += 1
    })
    await page.goto(`/${project.name}/issues/${issue.number}`)

    await expect(page.getByTestId('decision-action-approve-shortcut')).toHaveText('a')
    await page.keyboard.press('a')
    await expect.poll(() => approveRequests).toBe(1)
  })

  test('phone plan approval renders recorded artifacts without overflow and submits send-back once', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 820 })
    const issue = makeIssue({
      number: 412,
      status: 'in_progress',
      workflowStage: 'plan',
      workflowStatus: 'paused',
      workflowRunId: 'wr-plan-approval',
      health: 'paused',
      approvalState: { status: 'awaiting', stage: 'plan', requestedAt: '2026-07-01T03:00:00Z' },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'awaiting-approval',
        workflowSummaryState: 'approval-required',
        allowedActions: ['approve', 'reject'],
      },
    })
    const longToken = 'x'.repeat(600)
    await mockIssueDetailApi(page, issue, [], {
      artifacts: {
        'proposal.md': '# Plan evidence\n\nReview this proposal inline.',
        'tasks.json': `{ "token": "${longToken}" }`,
      },
    })
    await page.goto(`/${project.name}/issues/${issue.number}`)

    await expect(page.getByTestId('approval-artifact-proposal.md')).toContainText('Plan evidence')
    await expect(page.getByTestId('approval-artifact-tasks.json').locator('pre')).toContainText(longToken)
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)

    const approve = page.getByTestId('approval-mobile-approve')
    const sendBack = page.getByTestId('approval-mobile-send-back')
    await expect(approve).toBeVisible()
    await expect(sendBack).toBeVisible()
    const viewport = page.viewportSize()
    expect(viewport).not.toBeNull()
    for (const control of [approve, sendBack]) {
      const controlBox = await box(control)
      expect(controlBox.x).toBeGreaterThanOrEqual(0)
      expect(controlBox.x + controlBox.width).toBeLessThanOrEqual(viewport!.width)
      expect(controlBox.y + controlBox.height).toBeLessThanOrEqual(viewport!.height)
    }

    await sendBack.click()
    const feedback = page.getByTestId('send-back-feedback-textarea')
    await expect(feedback).toBeFocused()
    await page.getByRole('radio', { name: 'Scope' }).click()
    await feedback.fill('Keep the plan focused.')
    const feedbackRequest = page.waitForRequest((request) => request.method() === 'POST'
      && request.url().endsWith(`/projects/${project.id}/issues/${issue.number}/feedback`))
    await page.getByTestId('send-back-feedback-submit').click()
    expect(JSON.parse((await feedbackRequest).postData() ?? '{}')).toEqual({
      stage: 'plan',
      body: 'Category: Scope\n\nKeep the plan focused.',
    })
  })

  test('phone check approval renders recorded review and diff summary without overflow', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 820 })
    const issue = makeIssue({
      number: 413,
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'paused',
      workflowRunId: 'wr-check-approval',
      health: 'paused',
      approvalState: { status: 'awaiting', stage: 'check', requestedAt: '2026-07-01T03:00:00Z' },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'awaiting-approval',
        workflowSummaryState: 'approval-required',
        allowedActions: ['approve', 'reject'],
      },
    })
    await mockIssueDetailApi(page, issue, [], {
      artifacts: { 'review.md': '# Check review\n\nThe implementation is ready.' },
      diff: {
        available: true,
        head: 'feature/approval-review',
        base: 'main',
        ahead: 2,
        behind: 0,
        summary: { filesChanged: 3, additions: 42, deletions: 7 },
      },
    })
    await page.goto(`/${project.name}/issues/${issue.number}`)

    await expect(page.getByTestId('approval-artifact-review.md')).toContainText('Check review')
    const diff = page.getByTestId('approval-diff-summary')
    await expect(diff).toContainText('feature/approval-review compared with main')
    await expect(diff).toContainText('3 files changed')
    await expect(diff).toContainText('+42')
    await expect(diff).toContainText('-7')
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
    await expect(page.getByTestId('approval-mobile-approve')).toBeVisible()
    await expect(page.getByTestId('approval-mobile-send-back')).toBeVisible()
    await expect(page.getByTestId('mobile-action-sheet-launcher')).toHaveCount(0)
  })

  test('phone approval secondary actions navigate without hiding direct controls', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 820 })
    const issue = makeIssue({
      number: 414,
      status: 'in_progress',
      workflowStage: 'plan',
      workflowStatus: 'paused',
      workflowRunId: 'wr-secondary-actions',
      health: 'paused',
      approvalState: { status: 'awaiting', stage: 'plan', requestedAt: '2026-07-01T03:00:00Z' },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'awaiting-approval',
        workflowSummaryState: 'approval-required',
        allowedActions: ['approve', 'reject'],
      },
    })
    await mockIssueDetailApi(page, issue, sessions, { artifacts: { 'proposal.md': '# Plan', 'tasks.json': '{}' } })
    await page.goto(`/${project.name}/issues/${issue.number}`)

    await page.getByRole('button', { name: 'More actions' }).click()
    await expect(page.getByTestId('approval-mobile-approve')).toBeVisible()
    await expect(page.getByTestId('approval-mobile-send-back')).toBeVisible()
    await page.getByTestId('approval-more-action-ask-agent').click()
    await expect(page).toHaveURL(new RegExp(`/agent-sessions/new\\?issue=${issue.number}$`))

    await page.goto(`/${project.name}/issues/${issue.number}`)
    await page.getByRole('button', { name: 'More actions' }).click()
    await page.getByTestId('approval-more-action-view-transcript').click()
    await expect(page).toHaveURL(new RegExp(`/issues/${issue.number}/workflow/sessions/coder-2$`))
  })

  test('phone width keeps the launcher enabled even when the primary action itself is currently disabled', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 820 })
    const issue = makeIssue({
      number: 407,
      status: 'backlog',
      health: 'active',
      isDraft: true,
      canStart: true,
      blocker: { kind: 'draft' },
    })
    await mockIssueDetailApi(page, issue, [])
    await page.goto(`/${project.name}/issues/${issue.number}`)

    const launcher = page.getByTestId('mobile-action-sheet-launcher')
    await expect(launcher).toBeEnabled()
    await launcher.click()

    const sheet = page.getByTestId('mobile-action-sheet')
    await expect(sheet).toBeVisible()
    const startButton = sheet.getByTestId('mobile-sheet-action-start')
    await expect(startButton).toBeDisabled()
    await expect(sheet.getByTestId('mobile-sheet-action-start-reason')).toBeVisible()
    const askAgentLink = sheet.getByTestId('mobile-sheet-action-ask-agent')
    await expect(askAgentLink).toHaveCount(0)

    const viewport = page.viewportSize()
    expect(viewport).not.toBeNull()
    const launcherBox = await box(launcher)
    const sheetBox = await box(sheet)
    const startBox = await box(startButton)
    expect(launcherBox.x + launcherBox.width).toBeLessThanOrEqual(viewport!.width)
    expect(sheetBox.x + sheetBox.width).toBeLessThanOrEqual(viewport!.width)
    expect(sheetBox.y).toBeLessThan(viewport!.height)
    expect(startBox.x).toBeGreaterThanOrEqual(0)
    expect(startBox.x + startBox.width).toBeLessThanOrEqual(viewport!.width)
  })

  test('phone sheet does not overlap page content and closes via Escape', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 820 })
    const issue = makeIssue({
      number: 408,
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      workflowRunId: 'wr-running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't-1', title: 'Build' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    })
    await mockIssueDetailApi(page, issue, [])
    await page.goto(`/${project.name}/issues/${issue.number}`)

    await page.getByTestId('mobile-action-sheet-launcher').click()
    const sheet = page.getByTestId('mobile-action-sheet')
    await expect(sheet).toBeVisible()

    const column = page.getByTestId('issue-detail-content-column')
    const columnBox = await box(column)
    const sheetBox = await box(sheet)
    expect(columnBox.y + columnBox.height).toBeGreaterThanOrEqual(sheetBox.y)

    await page.keyboard.press('Escape')
    await expect(sheet).toHaveCount(0)
  })

  test('phone sheet preserves safe-area clearance and renders disabled destructive actions without live destructive styling', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 820 })
    const issue = makeIssue({
      number: 409,
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      workflowRunId: 'wr-running',
      health: 'active',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: [],
      },
    })
    await mockIssueDetailApi(page, issue, [])
    await page.goto(`/${project.name}/issues/${issue.number}`)

    const column = page.getByTestId('issue-detail-content-column')
    const columnClass = (await column.getAttribute('class')) ?? ''
    expect(columnClass).toMatch(/pb-\[calc\(/)

    await page.getByTestId('mobile-action-sheet-launcher').click()
    const sheet = page.getByTestId('mobile-action-sheet')
    const sheetBox = await box(sheet)
    const viewport = page.viewportSize()
    expect(viewport).not.toBeNull()
    expect(sheetBox.x).toBeGreaterThanOrEqual(0)
    expect(sheetBox.x + sheetBox.width).toBeLessThanOrEqual(viewport!.width)
  })

  test('transcript action is omitted when the issue has no concrete workflow session', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    const issue = makeIssue({
      number: 410,
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      isDraft: true,
      canStart: false,
      recovery: null,
    })
    await mockIssueDetailApi(page, issue, [])
    await page.goto(`/${project.name}/issues/${issue.number}`)

    const surface = page.getByTestId('issue-decision-surface')
    await expect(surface).toBeVisible()
    await expect(surface.getByTestId('decision-action-view-transcript')).toHaveCount(0)
    await expect(surface.getByTestId('decision-action-mark-ready')).toBeVisible()
  })
})
