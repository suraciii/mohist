import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import {
  DEFAULT_RECOVERY,
  makeIssue,
  mockMatchMedia,
  renderPage,
} from './_issueDetailReferenceRailTestUtils'
import { mockIssue, mockIssueCommits, mockIssueDiff, mountIssueDetail } from './_issueDetailMsw'


mountIssueDetail({ issue: makeIssue() })

beforeEach(() => {
  mockMatchMedia(false)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('IssueDetailPage reference-rail — metadata and configuration only', () => {
  it('exposes metadata, model, workflow-profile control, and prerequisites in the rail', async () => {
    mockIssue(makeIssue({
      model: 'sonnet',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
      prerequisites: [
        { number: 9, title: 'Prerequisite issue', completed: true },
      ],
    }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('desktop')

    expect(referenceRail.contains(screen.getByTestId('issue-detail-details-metadata'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('issue-workflow-profile-control-frame'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('workflow-profile-editor-frame'))).toBe(true)

    const detailsToggle = screen.getByTestId('reference-rail-details-toggle')
    const profileToggle = screen.getByTestId('reference-rail-workflow-profile-toggle')
    const configurationToggle = screen.getByTestId('reference-rail-configuration-toggle')
    expect(referenceRail.contains(detailsToggle)).toBe(true)
    expect(referenceRail.contains(profileToggle)).toBe(true)
    expect(referenceRail.contains(configurationToggle)).toBe(true)
  })

  it('does not place lifecycle or workflow actions in the reference rail (they live in the issue decision surface)', async () => {
    mockIssue(makeIssue({
      health: 'blocked',
      blockedReason: 'Blocked by runtime execution.',
      recovery: {
        ...DEFAULT_RECOVERY,
        latestAttemptState: 'failed',
        allowedActions: ['stop', 'retry', 'resume', 'rerun'],
      },
    }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))

    expect(referenceRail.querySelector('[data-testid="reference-rail-actions"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="issue-decision-surface"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="runtime-decision-surface"]')).toBeNull()
    expect(referenceRail.textContent ?? '').not.toContain('Current:')
    expect(referenceRail.textContent ?? '').not.toContain('Build decision surface')
    expect(referenceRail.textContent ?? '').not.toContain('Blocked by runtime execution.')

    for (const kind of ['approve', 'send-back', 'retry', 'resume', 'rerun', 'stop', 'start', 'mark-ready', 'close', 'mark-as-done']) {
      const action = referenceRail.querySelector(`[data-testid="runtime-action-${kind}"]`)
        ?? referenceRail.querySelector(`[data-testid="decision-action-${kind}"]`)
      expect(action).toBeNull()
    }
  })

  it('does not place workflow progress, outputs, changes/diff, commits, description, or comments in the rail', async () => {
    mockIssue(makeIssue({
      body: 'A description body that should not appear in the rail at all.',
      comments: [
        {
          id: 'c1',
          author: 'tester',
          body: 'A reviewer comment.',
          createdAt: '2026-01-04T00:00:00Z',
        },
      ],
      recovery: DEFAULT_RECOVERY,
    }))
    const diffData = {
      available: true,
      reason: null,
      head: 'feature/issue-14',
      base: 'master',
      mergeBase: 'abc',
      ahead: 1,
      behind: 0,
      canFastForward: true,
      comparison: 'merge-base',
      summary: { filesChanged: 1, commits: 1, additions: 4, deletions: 1 },
      files: [],
    }
    mockIssueDiff(diffData)
    mockIssueCommits({ ...diffData, commits: [] })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.querySelector('[data-testid="workflow-view-frame"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="runtime-evidence-frame"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="diff-summary-banner"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="diff-files-section"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="commits-section"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="description-section"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="comments-section"]')).toBeNull()
  })

  it('does not render the workflow profile editor in the reading flow', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = screen.getByTestId('reference-rail')
    const editorFrame = screen.getByTestId('workflow-profile-editor-frame')

    expect(referenceRail.contains(editorFrame)).toBe(true)
    expect(readingFlow.contains(editorFrame)).toBe(false)
  })
})

describe('IssueDetailPage reference-rail — low-frequency items collapsed by default', () => {
  it('keeps the drift panel collapsed by default with its body absent', async () => {
    mockIssue(makeIssue({
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const driftCard = await waitFor(() => screen.getByTestId('reference-rail-drift'))
    expect(driftCard.dataset.collapsed).toBe('true')
    expect(driftCard.querySelector('[data-testid="reference-rail-drift-body"]')).toBeNull()
    expect(screen.queryByRole('heading', { name: /Base Drift Detected/ })).toBeNull()
  })

  it('expands the drift panel only on a deliberate click', async () => {
    mockIssue(makeIssue({
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const driftCard = await waitFor(() => screen.getByTestId('reference-rail-drift'))
    expect(driftCard.dataset.collapsed).toBe('true')

    const toggle = screen.getByTestId('reference-rail-drift-toggle')
    fireEvent.click(toggle)

    await waitFor(() => {
      expect(driftCard.dataset.collapsed).toBe('false')
    })
    expect(driftCard.querySelector('[data-testid="reference-rail-drift-body"]')).not.toBeNull()
    expect(within(driftCard).getByText('Needs Attention')).toBeTruthy()
  })

  it('keeps the convergence panel collapsed by default with its body absent', async () => {
    mockIssue(makeIssue({
      health: 'blocked',
      convergence: {
        blockingItemCount: 1,
        directlyRepairedCount: 0,
        reactionAttempts: 0,
        attemptedItemIds: [],
        resolvedItemIds: [],
        unresolvedItemIds: ['cb-1'],
        newBlockingItemIds: [],
        nonBlockingItemIds: [],
        blockedReason: 'A blocking check failed.',
      },
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const convergenceCard = await waitFor(() => screen.getByTestId('reference-rail-convergence'))
    expect(convergenceCard.dataset.collapsed).toBe('true')
    expect(convergenceCard.querySelector('[data-testid="reference-rail-convergence-body"]')).toBeNull()
    expect(screen.queryByText('Workflow Blocked')).toBeNull()
  })

  it('does not render an empty convergence rail card for blocked issues without convergence content', async () => {
    mockIssue(makeIssue({
      health: 'blocked',
      blockedReason: 'Runtime blocked without convergence payload.',
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('blocked')
    expect(screen.getByTestId('decision-rationale').textContent ?? '').toContain('Runtime blocked without convergence payload.')
    expect(screen.queryByTestId('reference-rail-convergence')).toBeNull()
  })

  it('expands the convergence panel only on a deliberate click', async () => {
    mockIssue(makeIssue({
      health: 'blocked',
      convergence: {
        blockingItemCount: 1,
        directlyRepairedCount: 0,
        reactionAttempts: 0,
        attemptedItemIds: [],
        resolvedItemIds: [],
        unresolvedItemIds: ['cb-1'],
        newBlockingItemIds: [],
        nonBlockingItemIds: [],
        blockedReason: 'A blocking check failed.',
      },
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const convergenceCard = await waitFor(() => screen.getByTestId('reference-rail-convergence'))
    expect(convergenceCard.dataset.collapsed).toBe('true')

    const toggle = screen.getByTestId('reference-rail-convergence-toggle')
    fireEvent.click(toggle)

    await waitFor(() => {
      expect(convergenceCard.dataset.collapsed).toBe('false')
    })
    expect(convergenceCard.querySelector('[data-testid="reference-rail-convergence-body"]')).not.toBeNull()
  })

  it('keeps the drift panel collapsed on a narrow viewport until a deliberate click', async () => {
    mockMatchMedia(true)
    mockIssue(makeIssue({
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const driftCard = await waitFor(() => screen.getByTestId('reference-rail-drift'))
    expect(driftCard.dataset.collapsed).toBe('true')

    fireEvent.click(screen.getByTestId('reference-rail-drift-toggle'))

    await waitFor(() => {
      expect(driftCard.dataset.collapsed).toBe('false')
    })
  })
})

describe('IssueDetailPage reference-rail — rail contents exclusivity (full set of conditional cards)', () => {
  it('only renders rail cards from the allowed metadata/config/non-runtime action set', async () => {
    mockIssue(makeIssue({
      model: 'sonnet',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      convergence: {
        blockingItemCount: 1,
        directlyRepairedCount: 0,
        reactionAttempts: 0,
        attemptedItemIds: [],
        resolvedItemIds: [],
        unresolvedItemIds: ['cb-1'],
        newBlockingItemIds: [],
        nonBlockingItemIds: [],
        blockedReason: 'A blocking check failed.',
      },
      prerequisites: [{ number: 9, title: 'Prerequisite issue', completed: false }],
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))

    const expectedRailCards = [
      'reference-rail-details',
      'reference-rail-workflow-profile',
      'reference-rail-drift',
      'reference-rail-convergence',
      'reference-rail-configuration',
      'reference-rail-prerequisites',
    ]
    for (const testId of expectedRailCards) {
      expect(referenceRail.contains(screen.getByTestId(testId))).toBe(true)
    }
  })

  it('does not render rail cards outside the allowed metadata/config/non-runtime action set', async () => {
    mockIssue(makeIssue({
      model: 'sonnet',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      convergence: {
        blockingItemCount: 1,
        directlyRepairedCount: 0,
        reactionAttempts: 0,
        attemptedItemIds: [],
        resolvedItemIds: [],
        unresolvedItemIds: ['cb-1'],
        newBlockingItemIds: [],
        nonBlockingItemIds: [],
        blockedReason: 'A blocking check failed.',
      },
      prerequisites: [{ number: 9, title: 'Prerequisite issue', completed: false }],
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))

    const forbiddenTestIds = [
      'workflow-view-frame',
      'runtime-evidence-frame',
      'diff-files-section',
      'commits-section',
      'description-section',
      'comments-section',
      'issue-decision-surface',
      'runtime-decision-surface',
      'latest-artifacts-panel',
      'reference-rail-actions',
    ]
    for (const testId of forbiddenTestIds) {
      expect(
        referenceRail.querySelector(`[data-testid="${testId}"]`),
        `expected ${testId} not to be present in the reference rail`,
      ).toBeNull()
    }
  })

  it('renders only metadata, configuration, and workflow-profile on the rail (no decision surface)', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop', 'retry', 'resume', 'rerun'],
      },
    }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))

    expect(referenceRail.contains(screen.getByTestId('issue-detail-details-metadata'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('issue-workflow-profile-control-frame'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('workflow-profile-editor-frame'))).toBe(true)
    expect(referenceRail.querySelector('[data-testid="reference-rail-actions"]')).toBeNull()

    for (const kind of ['approve', 'send-back', 'retry', 'resume', 'rerun', 'stop', 'start']) {
      expect(referenceRail.querySelector(`[data-testid="runtime-action-${kind}"]`)).toBeNull()
      expect(referenceRail.querySelector(`[data-testid="decision-action-${kind}"]`)).toBeNull()
    }
    expect(referenceRail.querySelector('[data-testid="runtime-decision-surface"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="issue-decision-surface"]')).toBeNull()
  })
})
