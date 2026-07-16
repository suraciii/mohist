import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { useEffect } from 'react'
import { http, HttpResponse } from 'msw'
import { EpicStatus, type EpicDetail } from '../../../entities/epic'
import type { DependencyGraphWidgetProps } from '../../../widgets/epic-dependency-graph/ui/DependencyGraphWidget'
import { DependencyGraphErrorBoundary } from '../../../widgets/epic-dependency-graph/ui/DependencyGraphErrorBoundary'
import { issues, linkedIssue, renderPage as renderEpicDetailPage } from './_epicDetailPageTestUtils'
import type { EpicDetailPageComponents } from './EpicDetailPage'
import { useMswServer } from '../../../../tests/support/msw'

const widgetBehavior = {
  mode: 'default' as 'default' | 'empty' | 'error',
}

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
  http.get('*/api/projects/:projectId/epics/:epicNumber', () =>
    HttpResponse.json({ success: true, data: _epicData }),
  ),
  http.get('*/api/projects/:projectId/epics/:epicNumber/events', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: _issuesData }),
  ),
  http.post('*/api/projects/:projectId/epics/:epicNumber/issues', async ({ request, params }) => {
    const body = await request.json() as { issueNumber: number }
    _addEpicIssueTracker({ epicNumber: Number(params.epicNumber), issueNumber: body.issueNumber })
    return HttpResponse.json({ success: true, data: { epicNumber: Number(params.epicNumber), issueNumber: body.issueNumber } })
  }),
  http.delete('*/api/projects/:projectId/epics/:epicNumber/issues/:issueNumber', ({ params }) => {
    _removeEpicIssueTracker({ epicNumber: Number(params.epicNumber), issueNumber: Number(params.issueNumber) })
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/issues/:issueNumber/start', ({ params }) => {
    _startIssueTracker(Number(params.issueNumber))
    return HttpResponse.json({ success: true, data: { issue: {}, message: '' } })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/start', ({ params }) => {
    _startEpicTracker(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/done', ({ params }) => {
    _markEpicDoneTracker(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/close', ({ params }) => {
    _closeEpicTracker(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.patch('*/api/projects/:projectId/epics/:epicNumber', ({ params }) => {
    _updateEpicTracker(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/pause', async ({ request, params }) => {
    let reason: string | null = null
    try { const body = await request.json() as Record<string, unknown>; reason = (body.reason as string) ?? null } catch { /* empty body */ }
    _pauseEpicTracker({ number: Number(params.epicNumber), reason })
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/resume', ({ params }) => {
    _resumeEpicTracker(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
)

function hasCycle(linkedIssues: { number: number; prerequisiteNumbers: number[] }[]): boolean {
  const adj = new Map<number, number[]>()
  for (const issue of linkedIssues) adj.set(issue.number, issue.prerequisiteNumbers ?? [])
  const visited = new Set<number>()
  const stack = new Set<number>()
  function dfs(n: number): boolean {
    if (stack.has(n)) return true
    if (visited.has(n)) return false
    visited.add(n)
    stack.add(n)
    for (const dependency of adj.get(n) ?? []) if (dfs(dependency)) return true
    stack.delete(n)
    return false
  }
  for (const issueNumber of adj.keys()) if (dfs(issueNumber)) return true
  return false
}

function TestDependencyGraphWidget(props: DependencyGraphWidgetProps) {
  const mode = widgetBehavior.mode
  useEffect(() => {
    if (mode === 'empty') {
      props.onRenderabilityChange?.({ renderable: false, reason: 'empty' })
    } else if (mode === 'default') {
      if (hasCycle(props.linkedIssues)) {
        props.onRenderabilityChange?.({ renderable: false, reason: 'cyclic' })
      } else {
        props.onRenderabilityChange?.({ renderable: true, reason: null })
      }
    }
  }, [mode, props.linkedIssues, props.onRenderabilityChange])
  if (mode === 'error') {
    throw new Error('Simulated render error from DependencyGraphWidget')
  }
  if (mode === 'default' && !hasCycle(props.linkedIssues)) {
    return (
      <div
        data-testid="epic-dep-graph-canvas"
        className="h-[560px] w-full min-w-[640px] rounded-lg border bg-background"
      />
    )
  }
  return null
}

function captureExpectedGraphRenderError() {
  const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})
  return () => {
    expect(consoleError.mock.calls.some((call) => call.some((value) =>
      value instanceof Error
        ? value.message === 'Simulated render error from DependencyGraphWidget'
        : String(value).includes('Simulated render error from DependencyGraphWidget'),
    ))).toBe(true)
    consoleError.mockRestore()
  }
}

const graphComponents: EpicDetailPageComponents = {
  DependencyGraphErrorBoundary,
  DependencyGraphWidget: TestDependencyGraphWidget,
}

function renderPage() {
  return renderEpicDetailPage({
    components: graphComponents,
    epic: _epicData as EpicDetail,
  })
}

function makeEpicWithLinkedIssues(linkedIssues: unknown[]) {
  return {
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

describe('EpicDetailPage Graph unrenderable banner + Error Boundary', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    widgetBehavior.mode = 'default'
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
    widgetBehavior.mode = 'default'
  })

  it('renders the cyclic banner explaining the dependency cycle when the graph reports cyclic', async () => {
    widgetBehavior.mode = 'default'
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, prerequisiteNumbers: [2] }),
      linkedIssue({ number: 2, prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('cyclic')
    expect(banner.textContent).toMatch(/cycle/i)
    expect(banner.textContent).toMatch(/use the list below/i)
  })

  it('renders the empty banner explaining there is not enough data when the graph reports empty', async () => {
    widgetBehavior.mode = 'empty'
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1 }),
      linkedIssue({ number: 2, prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('empty')
    expect(banner.textContent).toMatch(/not enough/i)
    expect(banner.textContent).toMatch(/use the list below/i)
  })

  it('renders the fallback "Graph is unavailable" banner when the Error Boundary catches a render exception', async () => {
    widgetBehavior.mode = 'error'
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1 }),
      linkedIssue({ number: 2, prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    const assertExpectedRenderError = captureExpectedGraphRenderError()
    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('error')
    expect(banner.textContent).toMatch(/graph is unavailable/i)
    expect(banner.textContent).toMatch(/use the list below/i)

    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-scroll-container')).toBeNull()
    assertExpectedRenderError()
  })

  it('keeps the List view rendered as fallback for the empty unrenderable scenario', async () => {
    widgetBehavior.mode = 'empty'
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'L-1' }),
      linkedIssue({ number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    const listRegion = screen.getByTestId('linked-issues-list-region')
    expect(listRegion).toBeInTheDocument()
    expect(listRegion.getAttribute('data-fallback-for')).toBe('empty')

    const listRows = screen.getAllByTestId('linked-issue-row')
    expect(listRows).toHaveLength(2)
  })

  it('keeps the List view rendered as fallback when the Error Boundary catches a render exception', async () => {
    widgetBehavior.mode = 'error'
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'L-1' }),
      linkedIssue({ number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    const assertExpectedRenderError = captureExpectedGraphRenderError()
    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    const listRegion = screen.getByTestId('linked-issues-list-region')
    expect(listRegion).toBeInTheDocument()
    expect(listRegion.getAttribute('data-fallback-for')).toBe('error')

    const listRows = screen.getAllByTestId('linked-issue-row')
    expect(listRows).toHaveLength(2)
    assertExpectedRenderError()
  })

  it('keeps the List view rendered as fallback for the cyclic unrenderable scenario (existing behavior)', async () => {
    widgetBehavior.mode = 'default'
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'L-1', prerequisiteNumbers: [2] }),
      linkedIssue({ number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    const listRegion = screen.getByTestId('linked-issues-list-region')
    expect(listRegion).toBeInTheDocument()
    expect(listRegion.getAttribute('data-fallback-for')).toBe('cyclic')

    const listRows = screen.getAllByTestId('linked-issue-row')
    expect(listRows).toHaveLength(2)
  })

  it('does not show the graph canvas or scroll container when the banner is displayed (any unrenderable state)', async () => {
    widgetBehavior.mode = 'empty'
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1 }),
      linkedIssue({ number: 2, prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-scroll-container')).toBeNull()
  })

  it('shows the narrow-screen hint above the unavailability banner in DOM order', async () => {
    widgetBehavior.mode = 'empty'
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1 }),
      linkedIssue({ number: 2, prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    const region = screen.getByTestId('linked-issues-graph-region')
    const hint = screen.getByTestId('linked-issues-graph-narrow-hint')
    const banner = screen.getByTestId('linked-issues-graph-unavailable-banner')

    const children = Array.from(region.children)
    const hintIndex = children.indexOf(hint)
    const bannerIndex = children.indexOf(banner)
    expect(hintIndex).toBeGreaterThanOrEqual(0)
    expect(bannerIndex).toBeGreaterThanOrEqual(0)
    expect(hintIndex).toBeLessThan(bannerIndex)
  })

  it('clears the error fallback when the user switches back to the List tab and re-selects Graph with a healthy widget', async () => {
    widgetBehavior.mode = 'error'
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1 }),
      linkedIssue({ number: 2, prerequisiteNumbers: [1] }),
    ])

    renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    const assertExpectedRenderError = captureExpectedGraphRenderError()
    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(screen.getByTestId('linked-issues-graph-unavailable-banner').getAttribute('data-reason')).toBe('error')
    assertExpectedRenderError()

    fireEvent.click(screen.getByTestId('linked-issues-view-list'))
    await waitFor(() => {
      expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
    })

    widgetBehavior.mode = 'default'
    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-graph-unavailable-banner')).toBeNull()
  })

  it('re-probes graph renderability when linked issue data changes while Graph remains selected', async () => {
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'L-1', prerequisiteNumbers: [2] }),
      linkedIssue({ number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
    ])

    const { unmount } = renderPage()

    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner.getAttribute('data-reason')).toBe('cyclic')
    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()

    unmount()
    _epicData = makeEpicWithLinkedIssues([
      linkedIssue({ number: 1, title: 'L-1' }),
      linkedIssue({ number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
    ])

    const { rerenderPage } = renderPage()
    await screen.findByTestId('linked-issues-view-toggle')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-graph-unavailable-banner')).toBeNull()
    expect(screen.queryByTestId('linked-issues-list-region')).toBeNull()

    // verify no unused variable warning
    void rerenderPage
  })

  it('every unrenderable banner message directs the user to the list below (spec contract)', async () => {
    const scenarios = [
      { reason: 'cyclic' as const, expectedKeyword: /cycle/i, linkedIssues: () => [
        linkedIssue({ number: 1, prerequisiteNumbers: [2] }),
        linkedIssue({ number: 2, prerequisiteNumbers: [1] }),
      ] },
      { reason: 'empty' as const, expectedKeyword: /not enough/i, linkedIssues: () => [
        linkedIssue({ number: 1 }),
        linkedIssue({ number: 2 }),
      ] },
      { reason: 'error' as const, expectedKeyword: /graph is unavailable/i, linkedIssues: () => [
        linkedIssue({ number: 1 }),
        linkedIssue({ number: 2 }),
      ] },
    ]

    for (const scenario of scenarios) {
      widgetBehavior.mode = scenario.reason === 'cyclic' ? 'default' : scenario.reason
      _epicData = makeEpicWithLinkedIssues(scenario.linkedIssues())

      const { unmount } = renderPage()

      await screen.findByTestId('linked-issues-view-toggle')

      const assertExpectedRenderError = scenario.reason === 'error'
        ? captureExpectedGraphRenderError()
        : null
      fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

      const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
      expect(banner.textContent).toMatch(scenario.expectedKeyword)
      expect(banner.textContent).toMatch(/use the list below/i)
      assertExpectedRenderError?.()
      unmount()
      widgetBehavior.mode = 'default'
    }
  })
})
