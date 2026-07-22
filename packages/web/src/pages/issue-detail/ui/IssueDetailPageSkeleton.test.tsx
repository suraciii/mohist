import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { IssueDetailPageSkeleton } from './IssueDetailPageSkeleton'

afterEach(cleanup)

describe('IssueDetailPageSkeleton', () => {
  it('renders skeleton placeholders mirroring the page structure', () => {
    render(<IssueDetailPageSkeleton />)

    const skeleton = screen.getByTestId('issue-detail-page-skeleton')
    expect(skeleton).toBeTruthy()
    expect(skeleton.querySelectorAll('[data-slot="skeleton"]').length).toBeGreaterThan(0)
  })

  it('renders status-header-tier-skeleton, reading-flow-skeleton, and reference-rail-skeleton regions', () => {
    render(<IssueDetailPageSkeleton />)

    expect(screen.getByTestId('status-header-tier-skeleton')).toBeTruthy()
    expect(screen.getByTestId('reading-flow-skeleton')).toBeTruthy()
    expect(screen.getByTestId('reference-rail-skeleton')).toBeTruthy()
  })

  it('does not render a bare Loading text element', () => {
    render(<IssueDetailPageSkeleton />)

    expect(screen.queryByText(/^Loading\.{0,3}$/)).toBeNull()
  })

  it('marks itself as the initial loading state via data-loading-state', () => {
    render(<IssueDetailPageSkeleton />)

    const skeleton = screen.getByTestId('issue-detail-page-skeleton')
    expect(skeleton.getAttribute('data-loading-state')).toBe('initial')
  })
})
