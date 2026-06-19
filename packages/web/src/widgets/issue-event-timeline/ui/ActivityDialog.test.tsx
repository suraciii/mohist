import { beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { ActivityDialog } from './ActivityDialog'

const mockUseEventTimeline = vi.fn()

vi.mock('../useEventTimeline', () => ({
  useEventTimeline: (...args: unknown[]) => mockUseEventTimeline(...args),
}))

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function renderDialog(props: { issueNumber?: number; issueId?: string | null; workflowStatus?: string | null } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <ActivityDialog
          issueNumber={props.issueNumber ?? 42}
          issueId={props.issueId ?? 'issue-42'}
          workflowStatus={props.workflowStatus ?? null}
        />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('ActivityDialog', () => {
  beforeEach(() => {
    cleanup()
    vi.clearAllMocks()
    mockUseEventTimeline.mockReturnValue({ entries: [], isLoading: false })
  })

  it('renders an Activity entry button in the header area with aria-label and a min hit-target', () => {
    renderDialog()

    const entry = screen.getByTestId('activity-entry')
    expect(entry).toHaveAttribute('aria-label', 'Activity')
    expect(entry).toHaveClass('min-h-11')
    expect(entry).toHaveClass('min-w-11')
  })

  it('does not call useEventTimeline (so no events fetch) before the entry opens the dialog', () => {
    renderDialog()

    expect(mockUseEventTimeline).not.toHaveBeenCalled()
  })

  it('opens the timeline inside a Dialog on click, and forwards enabled=true to useEventTimeline', async () => {
    renderDialog({ issueNumber: 42, issueId: 'issue-42' })

    fireEvent.click(screen.getByTestId('activity-entry'))

    await waitFor(() => expect(screen.getByTestId('activity-dialog-content')).toBeTruthy())
    expect(mockUseEventTimeline).toHaveBeenLastCalledWith(42, 'issue-42', true)
  })

  it('does not show a precise event count on the entry button', () => {
    renderDialog()

    const entry = screen.getByTestId('activity-entry')
    expect(entry.textContent ?? '').not.toMatch(/\b\d+\b/)
  })

  it('renders the dialog as a near-fullscreen sheet on mobile width', () => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 375 })
    window.dispatchEvent(new Event('resize'))

    renderDialog()

    fireEvent.click(screen.getByTestId('activity-entry'))

    const content = screen.getByTestId('activity-dialog-content')
    expect(content).toHaveClass('h-[100dvh]')
    expect(content).toHaveClass('w-full')
    expect(content).toHaveClass('rounded-none')
  })

  it('unmounts the panel (and stops forwarding live accumulation) when the dialog closes', async () => {
    const { container } = renderDialog()

    expect(mockUseEventTimeline).not.toHaveBeenCalled()

    fireEvent.click(screen.getByTestId('activity-entry'))
    await waitFor(() => expect(mockUseEventTimeline).toHaveBeenCalledTimes(1))

    const dialogContent = screen.getByTestId('activity-dialog-content')
    fireEvent.keyDown(dialogContent, { key: 'Escape' })

    await waitFor(() => {
      expect(container.querySelector('[data-testid="event-timeline-panel"]')).toBeNull()
    })
  })

  it('remounts the panel on reopen and forwards enabled=true again to refetch the full persisted history', async () => {
    renderDialog()

    fireEvent.click(screen.getByTestId('activity-entry'))
    await waitFor(() => expect(mockUseEventTimeline).toHaveBeenLastCalledWith(42, 'issue-42', true))

    const dialogContent = screen.getByTestId('activity-dialog-content')
    fireEvent.keyDown(dialogContent, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByTestId('event-timeline-panel')).toBeNull())

    fireEvent.click(screen.getByTestId('activity-entry'))
    await waitFor(() =>
      expect(mockUseEventTimeline).toHaveBeenLastCalledWith(42, 'issue-42', true),
    )
  })
})
