import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { useMutation } from '@tanstack/react-query'
import { EpicStatus, type EpicDetail, type LinkedIssue } from '../../../entities/epic'
import {
  createDependencyGraphTestComponents,
  issues,
  linkedIssue,
  renderPage as renderEpicDetailPage,
} from './_epicDetailPageTestUtils'
import type { RemoveEpicIssueHook } from './EpicDetailPage'

const widgetBehavior = {
  mode: 'default' as 'default' | 'empty' | 'error',
}

const components = createDependencyGraphTestComponents(() => widgetBehavior.mode)

const _addEpicIssueTracker = vi.fn()
const _removeEpicIssueTracker = vi.fn()
const _updateEpicTracker = vi.fn()

const removeEpicIssueHook: RemoveEpicIssueHook = () =>
  useMutation<{ epicNumber: number; issueNumber: number }, Error, { epicNumber: number; issueNumber: number }>({
    mutationFn: async (variables) => {
      _removeEpicIssueTracker(variables)
      return variables
    },
  })

function renderPage(epic: EpicDetail) {
  return renderEpicDetailPage({
    components,
    dependencies: { removeEpicIssueHook },
    epic,
    issues,
  })
}

function makeEpicWithLinkedIssues(linkedIssues: LinkedIssue[]): EpicDetail {
  return {
    projectId: 'proj-1',
    number: 7,
    title: 'Graph epic',
    description: 'Epic description',
    priority: 'p1',
    status: EpicStatus.Idle,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    progress: {
      deliveredCount: 0,
      totalIssueCount: linkedIssues.length,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: null,
      nextIssueReason: null,
      readyToMarkDone: false,
    },
    linkedIssues,
  }
}

describe('EpicDetailPage linked issues view toggle', () => {
  const makeLinkedIssue = linkedIssue

  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('defaults to the list view and shows the toggle when the epic has 2+ linked issues', async () => {
    renderPage(makeEpicWithLinkedIssues([
      makeLinkedIssue({ number: 1 }),
      makeLinkedIssue({ number: 2, prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-list-region')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
    const toggle = screen.getByTestId('linked-issues-view-toggle')
    expect(toggle).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')
  })

  it('switches to the graph view when the Graph tab is clicked and does not mutate data', async () => {
    renderPage(makeEpicWithLinkedIssues([
      makeLinkedIssue({ number: 1, title: 'Root' }),
      makeLinkedIssue({ number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'true')
      expect(screen.queryByTestId('linked-issues-list-region')).toBeNull()
    })

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-region')).toBeInTheDocument()
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-list-region')).toBeNull()

    expect(_addEpicIssueTracker).not.toHaveBeenCalled()
    expect(_removeEpicIssueTracker).not.toHaveBeenCalled()
    expect(_updateEpicTracker).not.toHaveBeenCalled()
  })

  it('returns to the list view when the List tab is clicked after the graph is shown', async () => {
    renderPage(makeEpicWithLinkedIssues([
      makeLinkedIssue({ number: 1 }),
      makeLinkedIssue({ number: 2, prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('linked-issues-view-list'))

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
  })

  it('keeps the Graph tab reachable with an empty-data explanation when the epic has zero linked issues', async () => {
    renderPage(makeEpicWithLinkedIssues([]))

    await screen.findByTestId('linked-issues-list-region')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-toggle')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const graphRegion = screen.getByTestId('linked-issues-graph-region')
    expect(graphRegion).toHaveAttribute('data-renderability', 'empty')
    const banner = screen.getByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toHaveAttribute('data-reason', 'empty')
    expect(banner.textContent).toMatch(/not enough/i)
    expect(banner.textContent).toMatch(/use the list below/i)
    expect(screen.getByTestId('linked-issues-list-region')).toHaveAttribute('data-fallback-for', 'empty')
    expect(screen.getByText('No linked issues yet.')).toBeInTheDocument()
  })

  it('keeps the Graph tab reachable with an empty-data explanation when the epic has exactly one linked issue', async () => {
    renderPage(makeEpicWithLinkedIssues([
      makeLinkedIssue({ number: 1, title: 'Lone issue' }),
    ]))

    await screen.findByTestId('linked-issues-list-region')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-toggle')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const graphRegion = screen.getByTestId('linked-issues-graph-region')
    expect(graphRegion).toHaveAttribute('data-renderability', 'empty')
    const banner = screen.getByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toHaveAttribute('data-reason', 'empty')
    expect(banner.textContent).toMatch(/not enough/i)
    expect(banner.textContent).toMatch(/use the list below/i)
    expect(screen.getByTestId('linked-issues-list-region')).toHaveAttribute('data-fallback-for', 'empty')
    expect(screen.getByText('Lone issue')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issue-row')).toBeInTheDocument()
  })

  it('falls back to the list view when the graph reports a cycle', async () => {
    renderPage(makeEpicWithLinkedIssues([
      makeLinkedIssue({ number: 1, prerequisiteNumbers: [2] }),
      makeLinkedIssue({ number: 2, prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      const region = screen.queryByTestId('linked-issues-graph-region')
      if (region) {
        expect(region.getAttribute('data-renderability')).toBe('cyclic')
      }
      expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    })

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('cyclic')
    expect(banner.textContent).toMatch(/cycle/i)
    expect(banner.textContent).toMatch(/use the list below/i)

    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
  })

  it('keeps the Linked Issues list and add-issue selector fully functional when the graph is the default tab', async () => {
    renderPage(makeEpicWithLinkedIssues([
      makeLinkedIssue({ number: 1, title: 'L-1' }),
      makeLinkedIssue({ number: 2, title: 'L-2' }),
    ]))

    await screen.findByTestId('linked-issues-list-region')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.getByTestId('epic-issue-selector-trigger')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(2)

    fireEvent.click(screen.getAllByRole('button', { name: 'Remove' })[0])
    expect(_removeEpicIssueTracker).not.toHaveBeenCalled()

    fireEvent.click(screen.getByTestId('linked-issue-remove-confirm'))
    await waitFor(() =>
      expect(_removeEpicIssueTracker).toHaveBeenCalledWith({ epicNumber: 7, issueNumber: 1, }),
    )
  })
})

describe('EpicDetailPage Graph mobile degradation', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('defaults to the List view when 2+ linked issues are present (List is always the initial state)', async () => {
    renderPage(makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'Root' }),
      linkedIssue({ number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-list')
    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
  })

  it('keeps both List and Graph tabs visible and clickable when graphAvailable is true (data-testids unchanged)', async () => {
    renderPage(makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'Root' }),
      linkedIssue({ number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-list')

    const listTab = screen.getByTestId('linked-issues-view-list')
    const graphTab = screen.getByTestId('linked-issues-view-graph')

    expect(listTab).toBeInTheDocument()
    expect(graphTab).toBeInTheDocument()
    expect(listTab).toBeInstanceOf(HTMLButtonElement)
    expect(graphTab).toBeInstanceOf(HTMLButtonElement)
    expect((listTab as HTMLButtonElement).disabled).toBe(false)
    expect((graphTab as HTMLButtonElement).disabled).toBe(false)

    fireEvent.click(graphTab)
    fireEvent.click(listTab)
    expect(_addEpicIssueTracker).not.toHaveBeenCalled()
    expect(_removeEpicIssueTracker).not.toHaveBeenCalled()
    expect(_updateEpicTracker).not.toHaveBeenCalled()
  })

  it('wraps the graph canvas in an overflow-x-auto container with md:overflow-visible (no scrollbar on desktop)', async () => {
    renderPage(makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'Root' }),
      linkedIssue({ number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })

    const canvas = screen.getByTestId('epic-dep-graph-canvas')
    const scrollContainer = canvas.parentElement
    expect(scrollContainer).toBeTruthy()
    expect(scrollContainer!.getAttribute('data-testid')).toBe('linked-issues-graph-scroll-container')
    expect(scrollContainer!.classList.contains('overflow-x-auto')).toBe(true)
    expect(scrollContainer!.classList.contains('md:overflow-visible')).toBe(true)
  })

  it('gives the inner graph canvas a min-width class so it horizontally scrolls on narrow viewports', async () => {
    renderPage(makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'Root' }),
      linkedIssue({ number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })

    const canvas = screen.getByTestId('epic-dep-graph-canvas')
    const classes = Array.from(canvas.classList)
    const hasMinWidth = classes.some(cls => /^min-w-\[/.test(cls))
    expect(hasMinWidth).toBe(true)
  })

  it('renders a narrow-screen hint with md:hidden above the graph canvas', async () => {
    renderPage(makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'Root' }),
      linkedIssue({ number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-region')).toBeInTheDocument()
    })

    const hint = screen.getByTestId('linked-issues-graph-narrow-hint')
    expect(hint).toBeInTheDocument()
    expect(hint.classList.contains('md:hidden')).toBe(true)
    expect(hint.textContent).toMatch(/Graph works best on wider screens/i)
    expect(hint.textContent).toMatch(/swipe/i)
  })

  it('places the narrow-screen hint above the scroll container in DOM order', async () => {
    renderPage(makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'Root' }),
      linkedIssue({ number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-scroll-container')).toBeInTheDocument()
    })

    const region = screen.getByTestId('linked-issues-graph-region')
    const hint = screen.getByTestId('linked-issues-graph-narrow-hint')
    const scrollContainer = screen.getByTestId('linked-issues-graph-scroll-container')

    const hintIndex = Array.from(region.children).indexOf(hint)
    const scrollIndex = Array.from(region.children).indexOf(scrollContainer)
    expect(hintIndex).toBeGreaterThanOrEqual(0)
    expect(scrollIndex).toBeGreaterThanOrEqual(0)
    expect(hintIndex).toBeLessThan(scrollIndex)
  })

  it('keeps the narrow-screen hint hidden when the list view is the default (only renders inside the graph region)', async () => {
    renderPage(makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'Root' }),
      linkedIssue({ number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-list-region')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-narrow-hint')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-scroll-container')).toBeNull()
  })

  it('switches between List and Graph tabs without error and keeps the overflow-x-auto wrapper on every Graph render', async () => {
    renderPage(makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'Root' }),
      linkedIssue({ number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ]))

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-scroll-container')).toBeInTheDocument()
    })
    expect(screen.getByTestId('linked-issues-graph-narrow-hint')).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('linked-issues-view-list'))
    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-scroll-container')).toBeInTheDocument()
    })
    const canvasAfterReturn = screen.getByTestId('epic-dep-graph-canvas')
    const containerAfterReturn = canvasAfterReturn.parentElement
    expect(containerAfterReturn!.classList.contains('overflow-x-auto')).toBe(true)
    expect(containerAfterReturn!.classList.contains('md:overflow-visible')).toBe(true)
  })
})
