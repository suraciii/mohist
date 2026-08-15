import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { RuntimeToastHost, useRuntimeToast } from './RuntimeToastHost'

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

function TriggerToast({
  testId = 'runtime-toast-connection-disconnected',
  title = 'Live events disconnected',
  tone = 'transport' as const,
  ttlMs = 0,
}: {
  testId?: string
  title?: string
  tone?: 'transport' | 'info' | 'success' | 'warning' | 'error'
  ttlMs?: number
}) {
  const toast = useRuntimeToast()
  return (
    <button type="button" data-testid={`trigger-${testId}`} onClick={() => toast.push({ tone, title, testId, ttlMs })}>
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

describe('RuntimeToastHost', () => {
  afterEach(() => {
    vi.useRealTimers()
    cleanup()
  })

  it('does not render the viewport when no toasts are pushed', () => {
    renderWithHost(<div data-testid="page-content">hello</div>)
    expect(screen.queryByTestId('runtime-toast-host')).toBeNull()
  })

  it('renders a pushed transport notice inside the toast host region with the correct testId and tone', async () => {
    renderWithHost(<TriggerToast />)
    fireEvent.click(screen.getByTestId('trigger-runtime-toast-connection-disconnected'))
    await waitFor(() => {
      expect(screen.getByTestId('runtime-toast-connection-disconnected')).toBeTruthy()
    })
    const toast = screen.getByTestId('runtime-toast-connection-disconnected')
    expect(toast.dataset.tone).toBe('transport')
    const host = screen.getByTestId('runtime-toast-host')
    expect(host.textContent).toContain('Live events disconnected')
  })

  it('auto-dismisses a notice with a positive ttl', async () => {
    vi.useFakeTimers()
    renderWithHost(<TriggerToast testId="runtime-toast-short" title="Reconnecting…" ttlMs={50} />)
    fireEvent.click(screen.getByTestId('trigger-runtime-toast-short'))
    expect(screen.getByTestId('runtime-toast-short')).toBeTruthy()

    await act(async () => {
      await vi.advanceTimersByTimeAsync(50)
    })

    expect(screen.queryByTestId('runtime-toast-short')).toBeNull()
  })

  it('clears auto-dismiss timers when the host unmounts', () => {
    vi.useFakeTimers()
    const timerCountBeforeRender = vi.getTimerCount()
    const view = renderWithHost(<TriggerToast testId="runtime-toast-delayed" ttlMs={5_000} />)

    fireEvent.click(screen.getByTestId('trigger-runtime-toast-delayed'))
    expect(vi.getTimerCount()).toBe(timerCountBeforeRender + 1)

    view.unmount()

    expect(vi.getTimerCount()).toBe(timerCountBeforeRender)
  })

  it('allows manual dismiss via the dismiss button', async () => {
    renderWithHost(<TriggerToast />)
    fireEvent.click(screen.getByTestId('trigger-runtime-toast-connection-disconnected'))
    await waitFor(() => {
      expect(screen.getByTestId('runtime-toast-connection-disconnected')).toBeTruthy()
    })
    fireEvent.click(screen.getByTestId('runtime-toast-dismiss'))
    expect(screen.queryByTestId('runtime-toast-connection-disconnected')).toBeNull()
  })

  it('exposes a default testId when caller does not provide one', async () => {
    function DefaultIdTrigger() {
      const toast = useRuntimeToast()
      return (
        <button
          type="button"
          data-testid="trigger-default"
          onClick={() => toast.push({ tone: 'info', title: 'hello' })}
        >
          push
        </button>
      )
    }
    renderWithHost(<DefaultIdTrigger />)
    fireEvent.click(screen.getByTestId('trigger-default'))
    await waitFor(() => {
      expect(screen.getByTestId('runtime-toast-info')).toBeTruthy()
    })
  })

  it('forwards notices to the onNotice sink so Activity surface can mirror them', async () => {
    const seen: { tone?: string; title?: string }[] = []
    function OnNoticeTrigger() {
      const toast = useRuntimeToast()
      return (
        <button
          type="button"
          data-testid="trigger-notice"
          onClick={() =>
            toast.push({
              tone: 'transport',
              title: 'Live events disconnected',
              testId: 'runtime-toast-connection-disconnected',
            })
          }
        >
          push
        </button>
      )
    }
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <RuntimeToastHost onNotice={(notice) => seen.push({ tone: notice.toast.tone, title: notice.toast.title })}>
            <OnNoticeTrigger />
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )
    fireEvent.click(screen.getByTestId('trigger-notice'))
    await waitFor(() => {
      expect(seen.length).toBe(1)
    })
    expect(seen[0]).toEqual({ tone: 'transport', title: 'Live events disconnected' })
  })
})
