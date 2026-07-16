import { beforeEach, describe, expect, it } from 'vitest'
import { useState } from 'react'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import type { EventTimelineHistoryHook } from '../useEventTimeline'
import { ActivityDialog } from './ActivityDialog'
import { EventTimelinePanel, type EventTimelinePanelProps } from './EventTimelinePanel'
import { setScopedValue } from '../../../../tests/support/scoped-property'

let eventRequests = 0

const historyHook: EventTimelineHistoryHook = () => {
  const [data] = useState(() => {
    eventRequests++
    return []
  })
  return { data, isLoading: false }
}

function TestTimelinePanel(props: EventTimelinePanelProps) {
  return <EventTimelinePanel {...props} historyHook={historyHook} />
}

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function renderDialog(props: { issueNumber?: number; workflowStatus?: string | null } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <ActivityDialog
          issueNumber={props.issueNumber ?? 42}
          workflowStatus={props.workflowStatus ?? null}
          TimelinePanel={TestTimelinePanel}
        />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('ActivityDialog', () => {
  beforeEach(() => {
    cleanup()
    eventRequests = 0
  })

  it('renders an Activity entry button in the header area with aria-label and a min hit-target', () => {
    renderDialog()

    const entry = screen.getByTestId('activity-entry')
    expect(entry).toHaveAttribute('aria-label', 'Activity')
    expect(entry).toHaveClass('min-h-11')
    expect(entry).toHaveClass('min-w-11')
  })

  it('does not fetch events before the entry opens the dialog', () => {
    renderDialog()

    expect(eventRequests).toBe(0)
  })

  it('opens the timeline inside a Dialog and fetches persisted events', async () => {
    renderDialog({ issueNumber: 42 })

    fireEvent.click(screen.getByTestId('activity-entry'))

    await waitFor(() => expect(screen.getByTestId('activity-dialog-content')).toBeTruthy())
    await waitFor(() => expect(eventRequests).toBe(1))
  })

  it('does not show a precise event count on the entry button', () => {
    renderDialog()

    const entry = screen.getByTestId('activity-entry')
    expect(entry.textContent ?? '').not.toMatch(/\b\d+\b/)
  })

  it('renders the dialog as a near-fullscreen sheet on mobile width', () => {
    setScopedValue(window, 'innerWidth', 375)
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

    expect(eventRequests).toBe(0)

    fireEvent.click(screen.getByTestId('activity-entry'))
    await waitFor(() => expect(eventRequests).toBe(1))

    const dialogContent = screen.getByTestId('activity-dialog-content')
    fireEvent.keyDown(dialogContent, { key: 'Escape' })

    await waitFor(() => {
      expect(container.querySelector('[data-testid="event-timeline-panel"]')).toBeNull()
    })
  })

  it('remounts the panel on reopen and refetches the full persisted history', async () => {
    renderDialog()

    fireEvent.click(screen.getByTestId('activity-entry'))
    await waitFor(() => expect(eventRequests).toBe(1))

    const dialogContent = screen.getByTestId('activity-dialog-content')
    fireEvent.keyDown(dialogContent, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByTestId('event-timeline-panel')).toBeNull())

    fireEvent.click(screen.getByTestId('activity-entry'))
    await waitFor(() => expect(eventRequests).toBe(2))
  })
})
