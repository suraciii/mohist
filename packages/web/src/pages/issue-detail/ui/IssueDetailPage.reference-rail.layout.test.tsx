import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import {
  DEFAULT_RECOVERY,
  expectPreceding,
  makeIssue,
  mockMatchMedia,
  RAIL_CARD_TESTIDS,
  READING_FLOW_LAST_TESTIDS,
  renderPage,
} from './_issueDetailReferenceRailTestUtils'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'


mountIssueDetail({ issue: makeIssue() })

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('IssueDetailPage reference-rail — desktop right column', () => {
  beforeEach(() => {
    mockMatchMedia(false)
  })

  it('marks the rail as desktop mode and lays it out as a right column narrower than the reading flow', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('desktop')

    const readingFlow = screen.getByTestId('reading-flow')
    expectPreceding(readingFlow, referenceRail)

    expect(referenceRail.className).toMatch(/lg:col-span-1\b/)

    const railSpanMatch = referenceRail.className.match(/lg:col-span-(\d)/)
    const railSpan = railSpanMatch ? Number(railSpanMatch[1]) : 1
    const flowSpanMatch = readingFlow.className.match(/lg:col-span-(\d)/)
    const flowSpan = flowSpanMatch ? Number(flowSpanMatch[1]) : 0
    expect(railSpan).toBeLessThan(flowSpan)

    const grid = screen.getByTestId('issue-detail-content-grid')
    expect(grid.className).toMatch(/lg:grid-cols-3/)
  })

  it('keeps the desktop rail visible with a capped internally scrollable sticky column', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.className).toContain('lg:sticky')
    expect(referenceRail.className).toContain('lg:top-6')
    expect(referenceRail.className).toContain('lg:self-start')
    expect(referenceRail.className).toContain('lg:max-h-[calc(100vh-3rem)]')
    expect(referenceRail.className).toContain('lg:overflow-y-auto')
  })
})

describe('IssueDetailPage reference-rail — narrow-screen collapsed sections', () => {
  beforeEach(() => {
    mockMatchMedia(true)
  })

  it('marks the rail as narrow mode and does not occupy a right column beside the reading flow', async () => {
    mockIssue(makeIssue({
      model: 'sonnet',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('narrow')
    expect(referenceRail.className).not.toMatch(/lg:col-span-1\b/)

    fireEvent.click(screen.getByTestId('reference-rail-details-toggle'))
    expect(referenceRail.contains(screen.getByTestId('issue-detail-details-metadata'))).toBe(true)

    fireEvent.click(screen.getByTestId('reference-rail-workflow-profile-toggle'))
    expect(referenceRail.contains(screen.getByTestId('issue-workflow-profile-control-frame'))).toBe(true)
  })

  it('renders all rail items as collapsed sections on a narrow viewport', async () => {
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
      prereq: [{ number: 9, title: 'Prerequisite issue', completed: false }],
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('narrow')

    const railItems = [
      'reference-rail-details',
      'reference-rail-workflow-profile',
      'reference-rail-drift',
      'reference-rail-convergence',
      'reference-rail-configuration',
      'reference-rail-prerequisites',
    ]
    for (const testId of railItems) {
      const card = screen.getByTestId(testId)
      expect(card.dataset.collapsed).toBe('true')
      expect(referenceRail.contains(card)).toBe(true)
    }
  })

  it('collapses expanded rail cards when the viewport changes from desktop to narrow', async () => {
    const viewport = mockMatchMedia(false)
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
      prereq: [{ number: 9, title: 'Prerequisite issue', completed: false }],
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('desktop')
    expect(screen.getByTestId('reference-rail-details').dataset.collapsed).toBe('false')

    act(() => {
      viewport.setNarrow(true)
    })

    await waitFor(() => {
      expect(referenceRail.dataset.railMode).toBe('narrow')
    })

    for (const testId of [
      'reference-rail-details',
      'reference-rail-workflow-profile',
      'reference-rail-drift',
      'reference-rail-convergence',
      'reference-rail-configuration',
      'reference-rail-prerequisites',
    ]) {
      expect(screen.getByTestId(testId).dataset.collapsed).toBe('true')
    }
  })

  it('stacks rail items beneath the reading flow on a narrow viewport', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = screen.getByTestId('reference-rail')

    expect(readingFlow.compareDocumentPosition(referenceRail) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
  })
})

describe('IssueDetailPage reference-rail — document-order audit (narrow)', () => {
  beforeEach(() => {
    mockMatchMedia(true)
  })

  it('places every rail card after every last reading-flow item in document order on a narrow viewport', async () => {
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
      prereq: [{ number: 9, title: 'Prerequisite issue', completed: false }],
      recovery: DEFAULT_RECOVERY,
    }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('narrow')

    const lastReadingFlowElement = READING_FLOW_LAST_TESTIDS
      .map((id) => screen.queryByTestId(id))
      .find((el): el is HTMLElement => el !== null)

    if (lastReadingFlowElement) {
      const referenceRailPos = lastReadingFlowElement.compareDocumentPosition(referenceRail)
      expect(referenceRailPos & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
    }

    for (const railTestId of RAIL_CARD_TESTIDS) {
      const railCard = screen.queryByTestId(railTestId)
      if (!railCard) continue
      for (const readingTestId of READING_FLOW_LAST_TESTIDS) {
        const readingEl = screen.queryByTestId(readingTestId)
        if (!readingEl) continue
        const relationship = readingEl.compareDocumentPosition(railCard)
        expect(
          relationship & Node.DOCUMENT_POSITION_FOLLOWING,
          `expected ${railTestId} to follow ${readingTestId} in document order on narrow viewport`,
        ).not.toBe(0)
      }
    }
  })

  it('places the rail container after the reading-flow container on narrow, not interleaved', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = screen.getByTestId('reference-rail')

    expect(readingFlow.compareDocumentPosition(referenceRail) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
  })
})

describe('IssueDetailPage reference-rail — desktop restoration excludes mobile-only chrome', () => {
  beforeEach(() => {
    mockMatchMedia(false)
  })

  it('does not render MobileActionBar or ConfirmationDrawer in the DOM at desktop', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('reference-rail'))

    expect(container.querySelector('[data-testid="mobile-action-bar"]')).toBeNull()
    expect(container.querySelector('[data-testid="confirmation-drawer"]')).toBeNull()
  })

  it('does not render MobileActionBar or ConfirmationDrawer on desktop even with a primary action', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('reference-rail'))

    expect(container.querySelector('[data-testid="mobile-action-bar"]')).toBeNull()
    expect(container.querySelector('[data-testid="confirmation-drawer"]')).toBeNull()
  })
})

describe('IssueDetailPage reference-rail — convergence panel collapsed on every viewport', () => {
  function issueWithConvergence() {
    return makeIssue({
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
    })
  }

  it('keeps convergence collapsed by default on desktop', async () => {
    mockMatchMedia(false)
    mockIssue(issueWithConvergence())

    renderPage()

    const convergenceCard = await waitFor(() => screen.getByTestId('reference-rail-convergence'))
    expect(convergenceCard.dataset.collapsed).toBe('true')
    expect(convergenceCard.querySelector('[data-testid="reference-rail-convergence-body"]')).toBeNull()
  })

  it('keeps convergence collapsed by default on narrow', async () => {
    mockMatchMedia(true)
    mockIssue(issueWithConvergence())

    renderPage()

    const convergenceCard = await waitFor(() => screen.getByTestId('reference-rail-convergence'))
    expect(convergenceCard.dataset.collapsed).toBe('true')
    expect(convergenceCard.querySelector('[data-testid="reference-rail-convergence-body"]')).toBeNull()
  })
})
