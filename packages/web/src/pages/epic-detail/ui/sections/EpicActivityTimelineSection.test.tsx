import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../../entities/project'
import { TEST_PROJECT } from '../../../../../tests/test-utils'
import { EpicActivityTimelineSection } from './EpicActivityTimelineSection'
import type { StoredCloudEventDto } from '../../../../entities/epic'

let events: StoredCloudEventDto[] = []
let eventsLoading = false
let eventsError = false

function mockEventsResponse(nextEvents: StoredCloudEventDto[]) {
  events = nextEvents
  eventsLoading = false
  eventsError = false
}

function mockEventsPending() {
  eventsLoading = true
}

function mockEventsError() {
  eventsError = true
}

const eventsHook = () => ({ data: events, isLoading: eventsLoading, isError: eventsError }) as never

function makeEvent(overrides: Partial<StoredCloudEventDto> & { type: string; data?: unknown }): StoredCloudEventDto {
  return {
    id: overrides.id ?? 1,
    eventId: overrides.eventId ?? 'evt-1',
    source: overrides.source ?? '/mohist/projects/proj-1/epics/1',
    type: overrides.type,
    specVersion: overrides.specVersion ?? '1.0',
    subject: overrides.subject ?? '1',
    time: overrides.time ?? '2026-06-30T12:00:00+00:00',
    dataContentType: overrides.dataContentType ?? 'application/json',
    data: overrides.data ?? {},
    extensions: overrides.extensions ?? { projectid: 'proj-1', epic: '1' },
  }
}

function renderSection(epicNumber: number) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  const result = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter>
          <EpicActivityTimelineSection epicNumber={epicNumber} eventsHook={eventsHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { ...result, queryClient }
}

beforeEach(() => {
  mockEventsResponse([])
})

afterEach(() => {
  cleanup()
})

describe('EpicActivityTimelineSection', () => {
  it('renders an empty state when useEpicEvents returns an empty list', async () => {
    renderSection(1)

    const section = await screen.findByTestId('epic-activity-timeline')
    expect(section.getAttribute('data-empty')).toBe('true')
    expect(screen.getByTestId('epic-activity-timeline-empty')).toBeInTheDocument()
    expect(screen.queryByTestId('epic-activity-timeline-list')).not.toBeInTheDocument()
  })

  it('renders the loading state without throwing while the query is in flight', () => {
    mockEventsPending()

    renderSection(1)

    const section = screen.getByTestId('epic-activity-timeline-loading')
    expect(section.getAttribute('data-empty')).toBe('false')
  })

  it('renders the error state without throwing when the query fails', async () => {
    mockEventsError()

    renderSection(1)

    await waitFor(() => {
      expect(screen.getByTestId('epic-activity-timeline-error')).toBeInTheDocument()
    })
  })

  it('renders a creation entry for an EpicCreated event', async () => {
    mockEventsResponse([
      makeEvent({
        id: 1,
        type: 'com.mohist.epic.created',
        data: { title: 'Auth epic', description: 'desc', priority: 'p2' },
        time: '2026-06-30T12:00:00+00:00',
      }),
    ])

    renderSection(1)

    await waitFor(() => {
      expect(screen.getByTestId('epic-activity-entry-created')).toBeInTheDocument()
    })
    expect(screen.getByTestId('epic-activity-timeline').getAttribute('data-empty')).toBe('false')
    expect(screen.getByText('Epic created')).toBeInTheDocument()
  })

  it('renders status change entries with old and new status', async () => {
    mockEventsResponse([
      makeEvent({
        id: 2,
        type: 'com.mohist.epic.status-changed',
        data: { oldStatus: 'idle', newStatus: 'running' },
        time: '2026-06-30T12:01:00+00:00',
      }),
    ])

    renderSection(1)

    const entry = await screen.findByTestId('epic-activity-entry-status')
    expect(entry).toBeInTheDocument()
    expect(screen.getByText(/Status changed from Idle to Running/i)).toBeInTheDocument()
  })

  it('renders priority change entries with old and new priority', async () => {
    mockEventsResponse([
      makeEvent({
        id: 3,
        type: 'com.mohist.epic.priority-changed',
        data: { oldPriority: 'p2', newPriority: 'p0' },
        time: '2026-06-30T12:02:00+00:00',
      }),
    ])

    renderSection(1)

    const entry = await screen.findByTestId('epic-activity-entry-priority')
    expect(entry).toBeInTheDocument()
    expect(screen.getByText(/Priority changed from P2 to P0/i)).toBeInTheDocument()
  })

  it('renders issue-linked entries with the issue number', async () => {
    mockEventsResponse([
      makeEvent({
        id: 4,
        type: 'com.mohist.epic.issue-linked',
        data: { issueNumber: 42, },
        time: '2026-06-30T12:03:00+00:00',
      }),
    ])

    renderSection(1)

    const entry = await screen.findByTestId('epic-activity-entry-issue-linked')
    expect(entry).toBeInTheDocument()
    expect(screen.getByText(/Linked issue #42/i)).toBeInTheDocument()
  })

  it('renders issue-unlinked entries with the issue number', async () => {
    mockEventsResponse([
      makeEvent({
        id: 5,
        type: 'com.mohist.epic.issue-unlinked',
        data: { issueNumber: 17, },
        time: '2026-06-30T12:04:00+00:00',
      }),
    ])

    renderSection(1)

    const entry = await screen.findByTestId('epic-activity-entry-issue-unlinked')
    expect(entry).toBeInTheDocument()
    expect(screen.getByText(/Unlinked issue #17/i)).toBeInTheDocument()
  })

  it('renders a reopen entry distinct from a generic status change', async () => {
    mockEventsResponse([
      makeEvent({
        id: 6,
        type: 'com.mohist.epic.reopened',
        time: '2026-06-30T12:05:00+00:00',
      }),
    ])

    renderSection(1)

    const entry = await screen.findByTestId('epic-activity-entry-reopened')
    expect(entry).toBeInTheDocument()
    expect(screen.getByText('Epic reopened')).toBeInTheDocument()
    expect(screen.queryByTestId('epic-activity-entry-status')).not.toBeInTheDocument()
  })

  it('renders a closed entry for an EpicClosed event', async () => {
    mockEventsResponse([
      makeEvent({
        id: 7,
        type: 'com.mohist.epic.closed',
        time: '2026-06-30T12:06:00+00:00',
      }),
    ])

    renderSection(1)

    const entry = await screen.findByTestId('epic-activity-entry-closed')
    expect(entry).toBeInTheDocument()
    expect(screen.getByText('Epic closed')).toBeInTheDocument()
  })

  it('renders multiple entries in chronological order with each entry exposing its timestamp', async () => {
    mockEventsResponse([
      makeEvent({
        id: 1,
        type: 'com.mohist.epic.created',
        data: { title: 'Auth epic', description: '', priority: 'p2' },
        time: '2026-06-30T12:00:00+00:00',
      }),
      makeEvent({
        id: 2,
        type: 'com.mohist.epic.status-changed',
        data: { oldStatus: 'idle', newStatus: 'running' },
        time: '2026-06-30T12:01:00+00:00',
      }),
      makeEvent({
        id: 3,
        type: 'com.mohist.epic.priority-changed',
        data: { oldPriority: 'p2', newPriority: 'p0' },
        time: '2026-06-30T12:02:00+00:00',
      }),
      makeEvent({
        id: 4,
        type: 'com.mohist.epic.issue-linked',
        data: { issueNumber: 5, },
        time: '2026-06-30T12:03:00+00:00',
      }),
      makeEvent({
        id: 5,
        type: 'com.mohist.epic.reopened',
        time: '2026-06-30T12:04:00+00:00',
      }),
    ])

    renderSection(1)

    await waitFor(() => {
      expect(screen.getByTestId('epic-activity-entry-created')).toBeInTheDocument()
    })
    expect(screen.getByTestId('epic-activity-entry-status')).toBeInTheDocument()
    expect(screen.getByTestId('epic-activity-entry-priority')).toBeInTheDocument()
    expect(screen.getByTestId('epic-activity-entry-issue-linked')).toBeInTheDocument()
    expect(screen.getByTestId('epic-activity-entry-reopened')).toBeInTheDocument()

    expect(screen.getByTestId('epic-activity-entry-created').getAttribute('data-time'))
      .toBe('2026-06-30T12:00:00+00:00')
    expect(screen.getByTestId('epic-activity-entry-reopened').getAttribute('data-time'))
      .toBe('2026-06-30T12:04:00+00:00')
  })

  it('skips entries whose payload cannot be parsed (e.g. missing issue number) without failing the section', async () => {
    mockEventsResponse([
      makeEvent({
        id: 1,
        type: 'com.mohist.epic.created',
        data: { title: 'Auth epic', description: '', priority: 'p2' },
        time: '2026-06-30T12:00:00+00:00',
      }),
      makeEvent({
        id: 2,
        type: 'com.mohist.epic.issue-linked',
        data: { issueNumber: 'issue-1' },
        time: '2026-06-30T12:01:00+00:00',
      }),
    ])

    renderSection(1)

    await waitFor(() => {
      expect(screen.getByTestId('epic-activity-entry-created')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('epic-activity-entry-issue-linked')).not.toBeInTheDocument()
    expect(screen.getByTestId('epic-activity-timeline').getAttribute('data-empty')).toBe('false')
  })
})
