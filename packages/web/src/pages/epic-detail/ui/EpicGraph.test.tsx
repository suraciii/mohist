// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { EpicStatus } from '../../../entities/epic'
import { issues, linkedIssue, renderPage } from './_epicDetailPageTestHarness'
import { useMswServer } from '../../../../tests/support/msw'

const widgetBehavior = vi.hoisted(() => ({
  mode: 'default' as 'default' | 'empty' | 'error',
}))

let _epicData: unknown = null
let _issuesData: unknown[] = []

const _addEpicIssueTracker = vi.fn()
const _removeEpicIssueTracker = vi.fn()
const _startIssueTracker = vi.fn()
const _startEpicTracker = vi.fn()
const _markEpicDoneTracker = vi.fn()
const _closeEpicTracker = vi.fn()
const _updateEpicTracker = vi.fn()
const _pauseEpicTracker = vi.fn()
const _resumeEpicTracker = vi.fn()

useMswServer(
  http.get('*/api/projects/:projectId/epics/:epicId', () =>
    HttpResponse.json({ success: true, data: _epicData }),
  ),
  http.get('*/api/projects/:projectId/epics/:epicId/events', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: _issuesData }),
  ),
  http.post('*/api/projects/:projectId/epics/:epicId/issues', async ({ request, params }) => {
    const body = await request.json() as { issueId: string }
    _addEpicIssueTracker({ epicId: params.epicId, issueId: body.issueId })
    return HttpResponse.json({ success: true, data: { epicId: params.epicId, issueId: body.issueId } })
  }),
  http.delete('*/api/projects/:projectId/epics/:epicId/issues/:issueId', ({ params }) => {
    _removeEpicIssueTracker({ epicId: params.epicId, issueId: params.issueId })
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/issues/:issueNumber/start', ({ params }) => {
    _startIssueTracker(Number(params.issueNumber))
    return HttpResponse.json({ success: true, data: { issue: {}, message: '' } })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/start', ({ params }) => {
    _startEpicTracker(params.epicId)
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/done', ({ params }) => {
    _markEpicDoneTracker(params.epicId)
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/close', ({ params }) => {
    _closeEpicTracker(params.epicId)
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.patch('*/api/projects/:projectId/epics/:epicId', ({ params }) => {
    _updateEpicTracker(params.epicId)
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/pause', async ({ request, params }) => {
    let reason: string | null = null
    try { const body = await request.json() as Record<string, unknown>; reason = (body.reason as string) ?? null } catch { /* empty body */ }
    _pauseEpicTracker({ id: params.epicId, reason })
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/resume', ({ params }) => {
    _resumeEpicTracker(params.epicId)
    return HttpResponse.json({ success: true, data: {} })
  }),
)

vi.mock('../../../widgets/epic-dependency-graph', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../widgets/epic-dependency-graph')>()
  const { useEffect: useEffectMock, createElement } = await import('react')
  function hasCycle(issues: { number: number; prerequisiteNumbers: number[] }[]): boolean {
    const adj = new Map<number, number[]>()
    for (const i of issues) adj.set(i.number, i.prerequisiteNumbers ?? [])
    const visited = new Set<number>()
    const stack = new Set<number>()
    function dfs(n: number): boolean {
      if (stack.has(n)) return true
      if (visited.has(n)) return false
      visited.add(n); stack.add(n)
      for (const d of adj.get(n) ?? []) if (dfs(d)) return true
      stack.delete(n)
      return false
    }
    for (const n of adj.keys()) if (dfs(n)) return true
    return false
  }
  return {
    ...actual,
    DependencyGraphWidget: (props: {
      linkedIssues: Parameters<typeof actual.DependencyGraphWidget>[0]['linkedIssues']
      onRenderabilityChange?: (state: { renderable: boolean; reason: 'renderable' | 'cyclic' | 'empty' | null }) => void
    }) => {
      const mode = widgetBehavior.mode
      useEffectMock(() => {
        if (mode === 'empty') {
          props.onRenderabilityChange?.({ renderable: false, reason: 'empty' })
        } else if (mode === 'default') {
          if (hasCycle(props.linkedIssues as { number: number; prerequisiteNumbers: number[] }[])) {
            props.onRenderabilityChange?.({ renderable: false, reason: 'cyclic' })
          } else {
            props.onRenderabilityChange?.({ renderable: true, reason: null })
          }
        }
      }, [mode])
      if (mode === 'error') {
        throw new Error('Simulated render error from DependencyGraphWidget')
      }
      if (mode === 'default') {
        if (hasCycle(props.linkedIssues as { number: number; prerequisiteNumbers: number[] }[])) return null
        return createElement('div', {
          'data-testid': 'epic-dep-graph-canvas',
          className: 'h-[560px] w-full min-w-[640px] rounded-lg border bg-background',
        })
      }
      return null
    },
  }
})

function makeEpicWithLinkedIssues(linkedIssues: unknown[]) {
  return {
    id: 'epic-12345678',
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
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
  })

  it('defaults to the list view and shows the toggle when the epic has 2+ linked issues', async () => {
    _epicData = makeEpicWithLinkedIssues([
      makeLinkedIssue({ id: 'issue-1', number: 1 }),
      makeLinkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-list-region')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
    const toggle = screen.getByTestId('linked-issues-view-toggle')
    expect(toggle).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')
  })

  it('switches to the graph view when the Graph tab is clicked and does not mutate data', async () => {
    _epicData = makeEpicWithLinkedIssues([
      makeLinkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
      makeLinkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([
      makeLinkedIssue({ id: 'issue-1', number: 1 }),
      makeLinkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
    ])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([
      makeLinkedIssue({ id: 'issue-1', number: 1, title: 'Lone issue' }),
    ])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([
      makeLinkedIssue({ id: 'issue-1', number: 1, prerequisiteNumbers: [2] }),
      makeLinkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
    ])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([
      makeLinkedIssue({ id: 'issue-1', number: 1, title: 'L-1' }),
      makeLinkedIssue({ id: 'issue-2', number: 2, title: 'L-2' }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-list-region')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.getByTestId('epic-issue-selector-trigger')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(2)

    fireEvent.click(screen.getAllByRole('button', { name: 'Remove' })[0])
    expect(_removeEpicIssueTracker).not.toHaveBeenCalled()

    fireEvent.click(screen.getByTestId('linked-issue-remove-confirm'))
    await vi.waitFor(() =>
      expect(_removeEpicIssueTracker).toHaveBeenCalledWith({ epicId: 'epic-12345678', issueId: 'issue-1' }),
    )
  })
})

describe('EpicDetailPage Graph mobile degradation (T-003)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
  })

  it('defaults to the List view when 2+ linked issues are present (List is always the initial state)', async () => {
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-list')
    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
  })

  it('keeps both List and Graph tabs visible and clickable when graphAvailable is true (data-testids unchanged)', async () => {
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ])

    renderPage()

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
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-list-region')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-narrow-hint')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-scroll-container')).toBeNull()
  })

  it('switches between List and Graph tabs without error and keeps the overflow-x-auto wrapper on every Graph render', async () => {
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
    ])

    renderPage()

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
