// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { WorkflowConvergencePanel } from './WorkflowConvergencePanel'
import type { WorkflowConvergenceState } from '../../../shared/api/types'

function makeConvergence(overrides: Partial<WorkflowConvergenceState> = {}): WorkflowConvergenceState {
  return {
    failedCheck: undefined,
    blockingItemCount: 0,
    directlyRepairedCount: 0,
    reactionAttempts: 0,
    attemptedItemIds: [],
    resolvedItemIds: [],
    unresolvedItemIds: [],
    newBlockingItemIds: [],
    nonBlockingItemIds: [],
    blockedReason: undefined,
    ...overrides,
  }
}

describe('WorkflowConvergencePanel', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('returns null when convergence is null', () => {
    const { container } = render(<WorkflowConvergencePanel convergence={null} />)
    expect(container.firstChild).toBeNull()
  })

  it('returns null when convergence is undefined', () => {
    const { container } = render(<WorkflowConvergencePanel convergence={undefined} />)
    expect(container.firstChild).toBeNull()
  })

  it('returns null when no failed check, no blocking items, and no reaction attempts', () => {
    const convergence = makeConvergence({
      failedCheck: undefined,
      blockingItemCount: 0,
      reactionAttempts: 0,
    })
    const { container } = render(<WorkflowConvergencePanel convergence={convergence} />)
    expect(container.firstChild).toBeNull()
  })

  describe('blocked workflow displays convergence state', () => {
    it('shows failed check name and blocked reason', () => {
      const convergence = makeConvergence({
        failedCheck: 'review-passed',
        blockedReason: '2 blocking items remain',
        blockingItemCount: 2,
        reactionAttempts: 0,
      })
      render(<WorkflowConvergencePanel convergence={convergence} />)
      expect(screen.getByText(/Failed check:/)).toBeTruthy()
      expect(screen.getByText(/review-passed/)).toBeTruthy()
      expect(screen.getByText(/2 blocking items remain/)).toBeTruthy()
    })

    it('shows blocking item count, directly repaired count, reaction attempts, resolved and unresolved counts', () => {
      const convergence = makeConvergence({
        failedCheck: 'review-passed',
        blockingItemCount: 3,
        directlyRepairedCount: 1,
        reactionAttempts: 2,
        resolvedItemIds: ['item-1', 'item-2'],
        unresolvedItemIds: ['item-3'],
      })
      render(<WorkflowConvergencePanel convergence={convergence} />)
      expect(screen.getByText(/Blocking items:/)).toBeTruthy()
      expect(screen.getByText(/Directly repaired:/)).toBeTruthy()
      expect(screen.getByText(/Reaction attempts:/)).toBeTruthy()
      expect(screen.getByText(/Resolved:/)).toBeTruthy()
      expect(screen.getByText(/Unresolved:/)).toBeTruthy()
    })

    it('shows non-blocking follow-up items section', () => {
      const convergence = makeConvergence({
        failedCheck: 'review-passed',
        blockingItemCount: 1,
        nonBlockingItemIds: ['follow-up-1', 'follow-up-2', 'follow-up-3'],
      })
      render(<WorkflowConvergencePanel convergence={convergence} />)
      expect(screen.getByText(/Follow-up items:/)).toBeTruthy()
      expect(screen.getByText(/3/)).toBeTruthy()
      expect(screen.getByText(/These do not block the current workflow/)).toBeTruthy()
    })

    it('shows all clear message when all items are resolved and no blocking items remain', () => {
      const convergence = makeConvergence({
        failedCheck: undefined,
        blockingItemCount: 0,
        directlyRepairedCount: 2,
        reactionAttempts: 1,
        resolvedItemIds: ['item-1', 'item-2'],
        unresolvedItemIds: [],
      })
      render(<WorkflowConvergencePanel convergence={convergence} />)
      expect(screen.getByText(/All blocking items resolved/)).toBeTruthy()
    })
  })

  describe('partially resolved display', () => {
    it('shows unresolved count when items remain unresolved', () => {
      const convergence = makeConvergence({
        failedCheck: 'review-passed',
        blockingItemCount: 2,
        reactionAttempts: 1,
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: ['item-2'],
      })
      render(<WorkflowConvergencePanel convergence={convergence} />)
      expect(screen.getByText(/Blocking items:/)).toBeTruthy()
      expect(screen.getByText(/Resolved:/)).toBeTruthy()
      expect(screen.getByText(/Unresolved:/)).toBeTruthy()
    })
  })

  describe('fully resolved display', () => {
    it('shows green success indicator when all blocking items resolved', () => {
      const convergence = makeConvergence({
        failedCheck: undefined,
        blockingItemCount: 0,
        directlyRepairedCount: 3,
        reactionAttempts: 1,
        resolvedItemIds: ['item-1', 'item-2', 'item-3'],
        unresolvedItemIds: [],
        newBlockingItemIds: [],
      })
      render(<WorkflowConvergencePanel convergence={convergence} />)
      expect(screen.getByText(/All blocking items resolved/)).toBeTruthy()
    })
  })

  describe('no convergence state display', () => {
    it('shows nothing when convergence state has no meaningful data', () => {
      const convergence = makeConvergence({
        failedCheck: undefined,
        blockingItemCount: 0,
        reactionAttempts: 0,
      })
      const { container } = render(<WorkflowConvergencePanel convergence={convergence} />)
      expect(container.firstChild).toBeNull()
    })
  })
})
