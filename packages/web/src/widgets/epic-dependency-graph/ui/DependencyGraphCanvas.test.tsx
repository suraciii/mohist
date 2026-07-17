import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { useEffect } from 'react'
import { DependencyGraphCanvas } from './DependencyGraphCanvas'
import type { LinkedIssue } from '../../../entities/epic/model/types'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue/@x/types'
import { ProjectProvider } from '../../../entities/project'

function makeIssue(overrides: Partial<LinkedIssue> = {}): LinkedIssue {
  return {
    number: 1,
    title: 'Issue',
    status: IssueStatus.Backlog,
    stage: WorkflowStage.Plan,
    health: IssueHealth.Active,
    priority: 'p2',
    canStart: false,
    startBlocker: null,
    prerequisiteNumbers: [],
    externalPrerequisites: [],
    ...overrides,
  }
}

function LocationCapture({ onLocationChange }: { onLocationChange: (pathname: string) => void }) {
  const location = useLocation()
  useEffect(() => {
    onLocationChange(location.pathname)
  }, [location.pathname, onLocationChange])
  return null
}

function renderCanvas(
  linkedIssues: LinkedIssue[],
  options: {
    initialPath?: string
    onRenderabilityChange?: (s: { renderable: boolean; reason: 'renderable' | 'cyclic' | 'empty' | null }) => void
  } = {},
) {
  const queryClient = new QueryClient()
  const initialPath = options.initialPath ?? '/epics/epic-1'
  const location = { pathname: initialPath }
  const view = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={[initialPath]}>
          <Routes>
            <Route
              path="*"
              element={
                <>
                  <LocationCapture onLocationChange={(pathname) => { location.pathname = pathname }} />
                  <DependencyGraphCanvas
                    linkedIssues={linkedIssues}
                    navigatePathFor={(n) => `/p/test/issues/${n}`}
                    onRenderabilityChange={options.onRenderabilityChange}
                  />
                </>
              }
            />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { ...view, location }
}

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

describe('DependencyGraphCanvas - rendering', () => {
  it('renders the canvas wrapper when there are at least 2 issues', async () => {
    const a = makeIssue({ number: 1 })
    const b = makeIssue({ number: 2, prerequisiteNumbers: [1] })
    renderCanvas([a, b])
    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })
  })

  it('does not render the canvas when there are 0 issues', () => {
    renderCanvas([])
    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
  })

  it('does not render the canvas when there is only 1 issue', () => {
    renderCanvas([makeIssue({ number: 1 })])
    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
  })

  it('reports cyclic renderability when a cycle is present', async () => {
    const a = makeIssue({ number: 1, prerequisiteNumbers: [2] })
    const b = makeIssue({ number: 2, prerequisiteNumbers: [1] })
    const onChange = vi.fn()
    renderCanvas([a, b], { onRenderabilityChange: onChange })
    await waitFor(() => {
      const calls = onChange.mock.calls.map(call => call[0])
      const sawCyclic = calls.some(c => c.reason === 'cyclic')
      expect(sawCyclic).toBe(true)
    })
    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
  })

  it('reports renderable=true for a DAG with no cycle', async () => {
    const a = makeIssue({ number: 1 })
    const b = makeIssue({ number: 2, prerequisiteNumbers: [1] })
    const onChange = vi.fn()
    renderCanvas([a, b], { onRenderabilityChange: onChange })
    await waitFor(() => {
      const calls = onChange.mock.calls.map(call => call[0])
      const sawRenderable = calls.some(c => c.renderable === true && c.reason === null)
      expect(sawRenderable).toBe(true)
    })
  })

  it('reports empty renderability when there are 0 issues', async () => {
    const onChange = vi.fn()
    renderCanvas([], { onRenderabilityChange: onChange })
    await waitFor(() => {
      const calls = onChange.mock.calls.map(call => call[0])
      const sawEmpty = calls.some(c => c.renderable === false && c.reason === 'empty')
      expect(sawEmpty).toBe(true)
    })
  })

  it('renders a member node for each linked issue inside the canvas', async () => {
    const a = makeIssue({ number: 1 })
    const b = makeIssue({ number: 2, prerequisiteNumbers: [1] })
    renderCanvas([a, b])
    await waitFor(() => {
      expect(screen.getAllByTestId('epic-dep-member-node')).toHaveLength(2)
    })
  })

  it('marks each member node with the right readiness data attribute', async () => {
    const ready = makeIssue({ number: 1, canStart: true, startBlocker: null })
    const waiting = makeIssue({
      number: 2,
      prerequisiteNumbers: [1],
      canStart: false,
      startBlocker: { kind: 'waiting-for', issue: { number: 1, title: 'A' } },
    })
    renderCanvas([ready, waiting])
    await waitFor(() => {
      const nodes = screen.getAllByTestId('epic-dep-member-node')
      expect(nodes.find(n => n.getAttribute('data-readiness') === 'can-start')).toBeTruthy()
      expect(nodes.find(n => n.getAttribute('data-readiness') === 'waiting')).toBeTruthy()
    })
  })

  it('shows the Waiting for #N marker on waiting nodes', async () => {
    const a = makeIssue({ number: 1 })
    const b = makeIssue({
      number: 2,
      prerequisiteNumbers: [1],
      canStart: false,
      startBlocker: { kind: 'waiting-for', issue: { number: 1, title: 'A' } },
    })
    renderCanvas([a, b])
    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-waiting-for')).toHaveTextContent('Waiting for #1')
    })
  })

  it('does not show the Waiting for #N marker on non-waiting nodes', async () => {
    const a = makeIssue({ number: 1 })
    const b = makeIssue({ number: 2, prerequisiteNumbers: [1], canStart: true, startBlocker: null })
    renderCanvas([a, b])
    await waitFor(() => {
      expect(screen.getAllByTestId('epic-dep-member-node')).toHaveLength(2)
    })
    expect(screen.queryByTestId('epic-dep-waiting-for')).toBeNull()
  })
})

describe('DependencyGraphCanvas - external prerequisites', () => {
  it('renders a ghost node for an external prerequisite that has a summary', async () => {
    const a = makeIssue({ number: 1, prerequisiteNumbers: [99] })
    const b = makeIssue({
      number: 2,
      prerequisiteNumbers: [1, 99],
      externalPrerequisites: [{ number: 99, title: 'Out-of-epic', stage: 'plan', status: 'active' }],
    })
    renderCanvas([a, b])
    await waitFor(() => {
      const ghosts = screen.getAllByTestId('epic-dep-ghost-node')
      expect(ghosts).toHaveLength(1)
      expect(ghosts[0]).toHaveAttribute('data-resolved', 'true')
      expect(ghosts[0]).toHaveTextContent('External')
      expect(ghosts[0]).toHaveTextContent('Out-of-epic')
    })
  })

  it('renders an unresolved ghost node when the prereq has no summary', async () => {
    const a = makeIssue({ number: 1 })
    const b = makeIssue({ number: 2, prerequisiteNumbers: [1, 404] })
    renderCanvas([a, b])
    await waitFor(() => {
      const ghosts = screen.getAllByTestId('epic-dep-ghost-node')
      expect(ghosts).toHaveLength(1)
      expect(ghosts[0]).toHaveAttribute('data-resolved', 'false')
      expect(ghosts[0]).toHaveTextContent('Unresolved')
      expect(ghosts[0]).toHaveTextContent('#404 (unresolved)')
    })
  })
})

describe('DependencyGraphCanvas - read-only projection', () => {
  it('does not render any add/edit/delete/start controls on member nodes', async () => {
    const ready = makeIssue({ number: 1, canStart: true })
    const b = makeIssue({ number: 2, prerequisiteNumbers: [1], canStart: true })
    renderCanvas([ready, b])
    await waitFor(() => {
      expect(screen.getAllByTestId('epic-dep-member-node')).toHaveLength(2)
    })
    const buttons = screen.queryAllByRole('button')
    const startControls = buttons.filter(b => /start/i.test(b.textContent ?? ''))
    const editControls = buttons.filter(b => /edit/i.test(b.textContent ?? ''))
    const deleteControls = buttons.filter(b => /delete|remove/i.test(b.textContent ?? ''))
    expect(startControls).toHaveLength(0)
    expect(editControls).toHaveLength(0)
    expect(deleteControls).toHaveLength(0)
  })

  it('does not render any controls on ghost nodes', async () => {
    const a = makeIssue({
      number: 1,
      prerequisiteNumbers: [99],
      externalPrerequisites: [{ number: 99, title: 'Out-of-epic', stage: 'plan', status: 'active' }],
    })
    const b = makeIssue({ number: 2, prerequisiteNumbers: [1] })
    renderCanvas([a, b])
    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-ghost-node')).toBeInTheDocument()
    })
    const ghostButtons = screen.getByTestId('epic-dep-ghost-node').querySelectorAll('button')
    expect(ghostButtons).toHaveLength(0)
  })
})

describe('DependencyGraphCanvas - click navigation', () => {
  it('navigates to the issue route when a member node is clicked', async () => {
    const a = makeIssue({ number: 7, title: 'Clickable A' })
    const b = makeIssue({ number: 8, prerequisiteNumbers: [7], title: 'Clickable B' })
    const { location } = renderCanvas([a, b])
    await waitFor(() => {
      expect(screen.getAllByTestId('epic-dep-member-node')).toHaveLength(2)
    })
    const nodes = screen.getAllByTestId('epic-dep-member-node')
    const target = nodes.find(n => n.getAttribute('data-issue-number') === '7')!
    expect(target).toBeTruthy()
    await act(async () => {
      fireEvent.click(target)
    })
    await waitFor(() => {
      expect(location.pathname).toBe('/p/test/issues/7')
    })
  })

  it('does not navigate when a ghost node is clicked (ghosts have no internal issue to open)', async () => {
    const a = makeIssue({
      number: 1,
      prerequisiteNumbers: [99],
      externalPrerequisites: [{ number: 99, title: 'External', stage: 'plan', status: 'active' }],
    })
    const b = makeIssue({ number: 2, prerequisiteNumbers: [1] })
    const { location } = renderCanvas([a, b])
    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-ghost-node')).toBeInTheDocument()
    })
    const before = location.pathname
    const ghost = screen.getByTestId('epic-dep-ghost-node')
    fireEvent.click(ghost)
    await waitFor(() => {
      expect(location.pathname).toBe(before)
    })
  })
})
