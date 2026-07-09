// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { EpicStatus } from '../../../entities/epic'
import { issues, linkedIssue, renderPage } from './_epicDetailPageTestHarness'

/**
 * Tests for the Graph region's unrenderable banner + Error Boundary fallback
 * (cyclic / empty / error states), rendered inside <EpicDetailPage/>. The graph
 * region is driven by the (mocked) DependencyGraphWidget whose `widgetBehavior`
 * mode drives each scenario; this file mounts the page via renderPage() and
 * asserts on the `linked-issues-graph-unavailable-banner` / list-fallback
 * testids.
 */

// --- per-file hoisted mocks (Vitest hoists vi.mock per-file; cannot be shared) ---
const widgetBehavior = vi.hoisted(() => ({
  mode: 'default' as 'default' | 'empty' | 'error',
}))

const mocks = vi.hoisted(() => ({
  useEpic: vi.fn(),
  useIssues: vi.fn(),
  useAddEpicIssue: vi.fn(),
  useRemoveEpicIssue: vi.fn(),
  useStartIssue: vi.fn(),
  useStartEpic: vi.fn(),
  useMarkEpicDone: vi.fn(),
  useCloseEpic: vi.fn(),
  useUpdateEpic: vi.fn(),
  usePauseEpic: vi.fn(),
  useResumeEpic: vi.fn(),
}))


vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useIssues: mocks.useIssues,
}))

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useEpic: mocks.useEpic,
    useAddEpicIssue: mocks.useAddEpicIssue,
    useRemoveEpicIssue: mocks.useRemoveEpicIssue,
    useStartIssue: mocks.useStartIssue,
    useStartEpic: mocks.useStartEpic,
    useMarkEpicDone: mocks.useMarkEpicDone,
    useCloseEpic: mocks.useCloseEpic,
    useUpdateEpic: mocks.useUpdateEpic,
    usePauseEpic: mocks.usePauseEpic,
    useResumeEpic: mocks.useResumeEpic,
  }
})

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

describe('EpicDetailPage Graph unrenderable banner + Error Boundary (T-004)', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    widgetBehavior.mode = 'default'
    mocks.useIssues.mockReturnValue({ data: issues })
    mocks.useAddEpicIssue.mockReturnValue({ mutate: addMutate, isPending: false, isError: false })
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
    mocks.useStartEpic.mockReturnValue({ mutate: startEpicMutate, isPending: false })
  })

  afterEach(() => {
    cleanup()
    widgetBehavior.mode = 'default'
  })

  it('renders the cyclic banner explaining the dependency cycle when the graph reports cyclic', async () => {
    widgetBehavior.mode = 'default'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, prerequisiteNumbers: [2] }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('cyclic')
    expect(banner.textContent).toMatch(/cycle/i)
    expect(banner.textContent).toMatch(/use the list below/i)
  })

  it('renders the empty banner explaining there is not enough data when the graph reports empty', async () => {
    widgetBehavior.mode = 'empty'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('empty')
    expect(banner.textContent).toMatch(/not enough/i)
    expect(banner.textContent).toMatch(/use the list below/i)
  })

  it('renders the fallback "Graph is unavailable" banner when the Error Boundary catches a render exception', async () => {
    widgetBehavior.mode = 'error'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('error')
    expect(banner.textContent).toMatch(/graph is unavailable/i)
    expect(banner.textContent).toMatch(/use the list below/i)

    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-scroll-container')).toBeNull()
  })

  it('keeps the List view rendered as fallback for the empty unrenderable scenario', async () => {
    widgetBehavior.mode = 'empty'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'L-1' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

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
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'L-1' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    const listRegion = screen.getByTestId('linked-issues-list-region')
    expect(listRegion).toBeInTheDocument()
    expect(listRegion.getAttribute('data-fallback-for')).toBe('error')

    const listRows = screen.getAllByTestId('linked-issue-row')
    expect(listRows).toHaveLength(2)
  })

  it('keeps the List view rendered as fallback for the cyclic unrenderable scenario (existing behavior)', async () => {
    widgetBehavior.mode = 'default'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'L-1', prerequisiteNumbers: [2] }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

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
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-scroll-container')).toBeNull()
  })

  it('shows the narrow-screen hint above the unavailability banner in DOM order', async () => {
    widgetBehavior.mode = 'empty'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

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
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(screen.getByTestId('linked-issues-graph-unavailable-banner').getAttribute('data-reason')).toBe('error')

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
    let currentEpic = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'L-1', prerequisiteNumbers: [2] }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
    ])
    mocks.useEpic.mockImplementation(() => ({ data: currentEpic, isLoading: false }))

    const { rerenderPage } = renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner.getAttribute('data-reason')).toBe('cyclic')
    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()

    currentEpic = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'L-1' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
    ])
    rerenderPage()

    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-graph-unavailable-banner')).toBeNull()
    expect(screen.queryByTestId('linked-issues-list-region')).toBeNull()
  })

  it('every unrenderable banner message directs the user to the list below (spec contract)', async () => {
    const scenarios = [
      { reason: 'cyclic' as const, expectedKeyword: /cycle/i, linkedIssues: () => [
        linkedIssue({ id: 'issue-1', number: 1, prerequisiteNumbers: [2] }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ] },
      { reason: 'empty' as const, expectedKeyword: /not enough/i, linkedIssues: () => [
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2 }),
      ] },
      { reason: 'error' as const, expectedKeyword: /graph is unavailable/i, linkedIssues: () => [
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2 }),
      ] },
    ]

    for (const scenario of scenarios) {
      widgetBehavior.mode = scenario.reason === 'cyclic' ? 'default' : scenario.reason
      mocks.useEpic.mockReturnValue({
        data: makeEpicWithLinkedIssues(scenario.linkedIssues()),
        isLoading: false,
      })

      const { unmount } = renderPage()
      fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

      const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
      expect(banner.textContent).toMatch(scenario.expectedKeyword)
      expect(banner.textContent).toMatch(/use the list below/i)
      unmount()
      widgetBehavior.mode = 'default'
    }
  })
})
