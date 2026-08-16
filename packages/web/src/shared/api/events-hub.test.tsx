import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../entities/project'
import { useEventsConnection } from './events-hub'
import { fakeConnections } from '../../../tests/support/signalr-fake'

const projects = [
  {
    id: 'proj-1',
    name: 'Project 1',
    path: '/tmp/p1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]


function StateProbe({ projectId, onState }: { projectId: string | null; onState: (s: string) => void }) {
  const { status: state } = useEventsConnection(projectId, () => {}, undefined)
  onState(state)
  return <div data-testid="state-probe" data-state={state}>{state}</div>
}

function ChannelProbe({ projectId, onTranscript, onTaskLog }: { projectId: string; onTranscript?: (envelope: unknown) => void; onTaskLog?: (envelope: unknown) => void }) {
  useEventsConnection(projectId, () => {}, onTranscript, onTaskLog)
  return null
}

function TaskLogOnlyProbe({ projectId, onTaskLog }: { projectId: string; onTaskLog?: (envelope: unknown) => void }) {
  useEventsConnection(projectId, () => {}, undefined, onTaskLog, { applyDefaultSubscriptions: false })
  return null
}

function renderWithHost(ui: React.ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        {ui}
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('useEventsConnection state tracking', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('transitions through connecting → connected → reconnecting → disconnected as the HubConnection emits', async () => {
    const observed: string[] = []
    renderWithHost(<StateProbe projectId="proj-1" onState={(s) => observed.push(s)} />)

    await waitFor(() => {
      expect(screen.getByTestId('state-probe')).toBeTruthy()
    })

    await waitFor(() => {
      expect((screen.getByTestId('state-probe') as HTMLElement).dataset.state).toBe('connected')
    })

    const conn = fakeConnections[fakeConnections.length - 1]

    await act(async () => {
      conn.emit('reconnecting')
    })
    await waitFor(() => {
      expect((screen.getByTestId('state-probe') as HTMLElement).dataset.state).toBe('reconnecting')
    })

    await act(async () => {
      conn.emit('reconnected')
    })
    await waitFor(() => {
      expect((screen.getByTestId('state-probe') as HTMLElement).dataset.state).toBe('connected')
    })

    await act(async () => {
      conn.emit('close')
    })
    await waitFor(() => {
      expect((screen.getByTestId('state-probe') as HTMLElement).dataset.state).toBe('disconnected')
    })

    expect(observed).toContain('connecting')
    expect(observed).toContain('connected')
    expect(observed).toContain('reconnecting')
    expect(observed).toContain('disconnected')
  })

  it('returns "disconnected" when projectId is null', async () => {
    renderWithHost(<StateProbe projectId={null} onState={() => {}} />)

    await waitFor(() => {
      expect((screen.getByTestId('state-probe') as HTMLElement).dataset.state).toBe('disconnected')
    })
  })

  it('binds OnTaskLogDelta as a new optional callback, separate from OnEvent and OnTranscriptEvent', async () => {
    const transcriptCalls: unknown[] = []
    const taskLogCalls: unknown[] = []

    renderWithHost(
      <ChannelProbe
        projectId="proj-1"
        onTranscript={(envelope) => transcriptCalls.push(envelope)}
        onTaskLog={(envelope) => taskLogCalls.push(envelope)}
      />,
    )

    await waitFor(() => {
      expect(fakeConnections.length).toBeGreaterThan(0)
    })

    const conn = fakeConnections[fakeConnections.length - 1]
    await waitFor(() => {
      expect(conn.handlers.has('OnEvent')).toBe(true)
    })

    expect(conn.handlers.has('OnTranscriptEvent')).toBe(true)
    expect(conn.handlers.has('OnTaskLogDelta')).toBe(true)

    const transcriptHandler = conn.handlers.get('OnTranscriptEvent')!
    const taskLogHandler = conn.handlers.get('OnTaskLogDelta')!

    await act(async () => {
      transcriptHandler({ type: 'message.delta', sessionId: 's-1', sequence: 1, createdAt: 't', payload: { text: 'x' } })
    })
    await act(async () => {
      taskLogHandler({ ownerKind: 'workflow', ownerId: 'wr-1', workId: 'w-1', taskId: 't-1', entries: [], truncated: false })
    })

    expect(transcriptCalls).toHaveLength(1)
    expect(taskLogCalls).toHaveLength(1)
    expect(transcriptCalls[0]).not.toBe(taskLogCalls[0])
  })

  it('can open a task-log-only connection without default domain or transcript subscriptions', async () => {
    renderWithHost(<TaskLogOnlyProbe projectId="proj-1" onTaskLog={() => {}} />)

    await waitFor(() => {
      expect(fakeConnections.length).toBeGreaterThan(0)
    })

    const conn = fakeConnections[fakeConnections.length - 1]
    await waitFor(() => {
      expect(conn.handlers.has('OnTaskLogDelta')).toBe(true)
    })

    expect(conn.invokes.some((invoke) => invoke.method === 'SetSubscriptionsAsync')).toBe(false)
  })
})
