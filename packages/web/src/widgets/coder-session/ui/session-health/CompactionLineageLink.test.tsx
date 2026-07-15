import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { CompactionLineageLink } from './CompactionLineageLink'
import type { RuntimeSessionLineageEntry } from '../../../../entities/coder-session'
import type { CompactionLineageLinkProps } from './CompactionLineageLink'

function makeEntry(id: string, boundAt: string): RuntimeSessionLineageEntry {
  return { runtimeSessionId: id, boundAt }
}

const FIXED_PATH_BUILDER = (runtimeId: string) =>
  `/Test/issues/12/workflow/sessions/build-assets?rt=${encodeURIComponent(runtimeId)}`

function renderLink(props: Partial<CompactionLineageLinkProps> = {}) {
  return render(
    <MemoryRouter>
      <CompactionLineageLink buildTargetPath={FIXED_PATH_BUILDER} {...props} />
    </MemoryRouter>,
  )
}

describe('CompactionLineageLink', () => {
  it('renders nothing when the lineage chain is null', () => {
    const { container } = renderLink({ runtimeSessionLineage: null })
    expect(container.firstChild).toBeNull()
    expect(screen.queryByTestId('compaction-lineage-link')).toBeNull()
  })

  it('renders nothing when the lineage chain is undefined', () => {
    const { container } = renderLink({ runtimeSessionLineage: undefined })
    expect(container.firstChild).toBeNull()
    expect(screen.queryByTestId('compaction-lineage-link')).toBeNull()
  })

  it('renders nothing when the lineage chain is empty', () => {
    const { container } = renderLink({ runtimeSessionLineage: [] })
    expect(container.firstChild).toBeNull()
    expect(screen.queryByTestId('compaction-lineage-link')).toBeNull()
  })

  it('renders nothing for a single-entry chain (no compaction relationship)', () => {
    const { container } = renderLink({
      runtimeSessionLineage: [makeEntry('rt-only', '2026-01-01T00:00:00Z')],
      viewedRuntimeSessionId: 'rt-only',
    })
    expect(container.firstChild).toBeNull()
    expect(screen.queryByTestId('compaction-lineage-link')).toBeNull()
  })

  it('renders a predecessor link when the user is viewing the latest runtime session in a 2-entry chain', () => {
    const lineage = [
      makeEntry('rt-A', '2026-01-01T00:00:00Z'),
      makeEntry('rt-B', '2026-01-02T00:00:00Z'),
    ]
    renderLink({
      runtimeSessionLineage: lineage,
      viewedRuntimeSessionId: 'rt-B',
    })

    const predecessorLink = screen.getByTestId('compaction-lineage-link-predecessor')
    expect(predecessorLink).toBeInTheDocument()
    expect(predecessorLink).toHaveAttribute('data-target-runtime-session-id', 'rt-A')
    expect(predecessorLink.getAttribute('href')).toBe(
      '/Test/issues/12/workflow/sessions/build-assets?rt=rt-A',
    )

    expect(screen.queryByTestId('compaction-lineage-link-successor')).toBeNull()

    const root = screen.getByTestId('compaction-lineage-link')
    expect(root).toHaveAttribute('data-viewed-index', '1')
    expect(root).toHaveAttribute('data-lineage-length', '2')
  })

  it('renders a successor link when the user is viewing the oldest runtime session in a 2-entry chain', () => {
    const lineage = [
      makeEntry('rt-A', '2026-01-01T00:00:00Z'),
      makeEntry('rt-B', '2026-01-02T00:00:00Z'),
    ]
    renderLink({
      runtimeSessionLineage: lineage,
      viewedRuntimeSessionId: 'rt-A',
    })

    const successorLink = screen.getByTestId('compaction-lineage-link-successor')
    expect(successorLink).toBeInTheDocument()
    expect(successorLink).toHaveAttribute('data-target-runtime-session-id', 'rt-B')
    expect(successorLink.getAttribute('href')).toBe(
      '/Test/issues/12/workflow/sessions/build-assets?rt=rt-B',
    )

    expect(screen.queryByTestId('compaction-lineage-link-predecessor')).toBeNull()
  })

  it('renders both predecessor and successor links when the user is viewing a non-latest runtime session', () => {
    const lineage = [
      makeEntry('rt-A', '2026-01-01T00:00:00Z'),
      makeEntry('rt-B', '2026-01-02T00:00:00Z'),
      makeEntry('rt-C', '2026-01-03T00:00:00Z'),
    ]
    renderLink({
      runtimeSessionLineage: lineage,
      viewedRuntimeSessionId: 'rt-B',
    })

    const predecessorLink = screen.getByTestId('compaction-lineage-link-predecessor')
    expect(predecessorLink).toHaveAttribute('data-target-runtime-session-id', 'rt-A')
    expect(predecessorLink.getAttribute('href')).toBe(
      '/Test/issues/12/workflow/sessions/build-assets?rt=rt-A',
    )

    const successorLink = screen.getByTestId('compaction-lineage-link-successor')
    expect(successorLink).toHaveAttribute('data-target-runtime-session-id', 'rt-C')
    expect(successorLink.getAttribute('href')).toBe(
      '/Test/issues/12/workflow/sessions/build-assets?rt=rt-C',
    )

    const root = screen.getByTestId('compaction-lineage-link')
    expect(root).toHaveAttribute('data-viewed-index', '1')
    expect(root).toHaveAttribute('data-lineage-length', '3')
  })

  it('renders only a predecessor link when the user is viewing the latest runtime session in a 3-entry chain', () => {
    const lineage = [
      makeEntry('rt-A', '2026-01-01T00:00:00Z'),
      makeEntry('rt-B', '2026-01-02T00:00:00Z'),
      makeEntry('rt-C', '2026-01-03T00:00:00Z'),
    ]
    renderLink({
      runtimeSessionLineage: lineage,
      viewedRuntimeSessionId: 'rt-C',
    })

    expect(screen.getByTestId('compaction-lineage-link-predecessor')).toHaveAttribute(
      'data-target-runtime-session-id',
      'rt-B',
    )
    expect(screen.queryByTestId('compaction-lineage-link-successor')).toBeNull()
  })

  it('defaults to treating the last entry as the viewed runtime session when no id is provided', () => {
    const lineage = [
      makeEntry('rt-A', '2026-01-01T00:00:00Z'),
      makeEntry('rt-B', '2026-01-02T00:00:00Z'),
    ]
    renderLink({
      runtimeSessionLineage: lineage,
      viewedRuntimeSessionId: null,
    })

    const root = screen.getByTestId('compaction-lineage-link')
    expect(root).toHaveAttribute('data-viewed-index', '1')

    const predecessorLink = screen.getByTestId('compaction-lineage-link-predecessor')
    expect(predecessorLink).toHaveAttribute('data-target-runtime-session-id', 'rt-A')
    expect(screen.queryByTestId('compaction-lineage-link-successor')).toBeNull()
  })

  it('defaults to the latest entry when the viewed id does not match any chain entry (legacy / malformed query param)', () => {
    const lineage = [
      makeEntry('rt-A', '2026-01-01T00:00:00Z'),
      makeEntry('rt-B', '2026-01-02T00:00:00Z'),
    ]
    renderLink({
      runtimeSessionLineage: lineage,
      viewedRuntimeSessionId: 'rt-not-in-chain',
    })

    const root = screen.getByTestId('compaction-lineage-link')
    expect(root).toHaveAttribute('data-viewed-index', '1')
    expect(screen.getByTestId('compaction-lineage-link-predecessor')).toHaveAttribute(
      'data-target-runtime-session-id',
      'rt-A',
    )
    expect(screen.queryByTestId('compaction-lineage-link-successor')).toBeNull()
  })

  it('attaches the predecessor runtime-session id to the navigation target via the ?rt= query param', () => {
    const lineage = [
      makeEntry('rt-A', '2026-01-01T00:00:00Z'),
      makeEntry('rt-B', '2026-01-02T00:00:00Z'),
    ]
    const builder = (id: string) => `/custom-base/sessions/foo?rt=${encodeURIComponent(id)}&keep=1`
    render(
      <MemoryRouter>
        <CompactionLineageLink
          runtimeSessionLineage={lineage}
          viewedRuntimeSessionId="rt-B"
          buildTargetPath={builder}
        />
      </MemoryRouter>,
    )
    const predecessorLink = screen.getByTestId('compaction-lineage-link-predecessor')
    expect(predecessorLink.getAttribute('href')).toBe('/custom-base/sessions/foo?rt=rt-A&keep=1')
  })

  it('uses descriptive accessible labels for each link direction', () => {
    const lineage = [
      makeEntry('rt-A', '2026-01-01T00:00:00Z'),
      makeEntry('rt-B', '2026-01-02T00:00:00Z'),
      makeEntry('rt-C', '2026-01-03T00:00:00Z'),
    ]
    renderLink({
      runtimeSessionLineage: lineage,
      viewedRuntimeSessionId: 'rt-B',
    })

    expect(screen.getByTestId('compaction-lineage-link-predecessor').getAttribute('aria-label'))
      .toBe('Navigate to previous runtime session rt-A')
    expect(screen.getByTestId('compaction-lineage-link-successor').getAttribute('aria-label'))
      .toBe('Navigate to next runtime session rt-C')
  })

  it('renders the runtime session history label so the link describes replacement lineage', () => {
    const lineage = [
      makeEntry('rt-A', '2026-01-01T00:00:00Z'),
      makeEntry('rt-B', '2026-01-02T00:00:00Z'),
    ]
    renderLink({
      runtimeSessionLineage: lineage,
      viewedRuntimeSessionId: 'rt-B',
    })
    expect(screen.getByTestId('compaction-lineage-link-label')).toHaveTextContent('Runtime session history')
  })
})
