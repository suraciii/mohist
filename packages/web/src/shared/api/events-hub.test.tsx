// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../entities/project'
import { useConnectionState } from './events-hub'
import { RuntimeToastHost, useRuntimeToast } from '../ui/toast'
import { HubConnectionBuilder } from '@microsoft/signalr'

vi.mock('@microsoft/signalr', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@microsoft/signalr')>()
  return {
    ...actual,
    HubConnectionBuilder: vi.fn(),
  }
})

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

type Listener = (...args: unknown[]) => void

interface FakeConnection {
  state: number
  onreconnecting: (handler?: Listener) => Listener | null | void
  onreconnected: (handler?: Listener) => Listener | null | void
  onclose: (handler?: Listener) => Listener | null | void
  on: (event: string, handler: Listener) => void
  start: () => Promise<void>
  stop: () => Promise<void>
  invoke: (...args: unknown[]) => Promise<unknown>
  emit: (kind: 'reconnecting' | 'reconnected' | 'close') => void
}

const fakeConnections: FakeConnection[] = []

function makeFakeConnection(): FakeConnection {
  let onReconnectingHandler: Listener | null = null
  let onReconnectedHandler: Listener | null = null
  let onCloseHandler: Listener | null = null
  const conn: FakeConnection = {
    state: 0,
    onreconnecting(handler) {
      if (handler === undefined) return onReconnectingHandler
      onReconnectingHandler = handler
    },
    onreconnected(handler) {
      if (handler === undefined) return onReconnectedHandler
      onReconnectedHandler = handler
    },
    onclose(handler) {
      if (handler === undefined) return onCloseHandler
      onCloseHandler = handler
    },
    on: vi.fn(),
    start: vi.fn(async () => {
      conn.state = 1
    }),
    stop: vi.fn(async () => {
      conn.state = 0
    }),
    invoke: vi.fn(async () => undefined),
    emit(kind) {
      if (kind === 'reconnecting') {
        onReconnectingHandler?.()
      }
      if (kind === 'reconnected') {
        onReconnectedHandler?.()
      }
      if (kind === 'close') {
        onCloseHandler?.()
      }
    },
  }
  fakeConnections.push(conn)
  return conn
}

function StateProbe({ projectId, onState }: { projectId: string | null; onState: (s: string) => void }) {
  const state = useConnectionState(projectId, { publishToasts: false })
  onState(state)
  return <div data-testid="state-probe" data-state={state}>{state}</div>
}

function ToastProbe() {
  const toast = useRuntimeToast()
  return (
    <button
      type="button"
      data-testid="push-disconnected"
      onClick={() => toast.push({
        tone: 'transport',
        title: 'Live events disconnected',
        body: 'Connection dropped.',
        testId: 'runtime-toast-connection-disconnected',
      })}
    >
      push
    </button>
  )
}

function renderWithHost(ui: React.ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <RuntimeToastHost>{ui}</RuntimeToastHost>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('useConnectionState', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    fakeConnections.length = 0
    function FakeBuilder(this: unknown) {
      return {
        withUrl: () => ({
          withAutomaticReconnect: () => ({
            configureLogging: () => ({
              build: () => makeFakeConnection(),
            }),
          }),
        }),
      }
    }
    vi.mocked(HubConnectionBuilder).mockImplementation(FakeBuilder as unknown as typeof HubConnectionBuilder)
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
})

describe('RuntimeToastHost transport routing', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('renders a pushed transport notice inside the toast host region', async () => {
    renderWithHost(<ToastProbe />)
    const trigger = screen.getByTestId('push-disconnected')
    fireEvent.click(trigger)
    await waitFor(() => {
      expect(screen.getByTestId('runtime-toast-connection-disconnected')).toBeTruthy()
    })
    const host = screen.getByTestId('runtime-toast-host')
    expect(host.textContent).toContain('Live events disconnected')
  })
})
