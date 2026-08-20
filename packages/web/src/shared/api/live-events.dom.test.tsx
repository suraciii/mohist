import type { ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { useEventsConnection } from './live-events'

class FakeWebSocket {
  readyState = 0
  onopen: ((event: Event) => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onclose: ((event: CloseEvent) => void) | null = null
  onerror: ((event: Event) => void) | null = null
  sent: string[] = []

  open() {
    this.readyState = 1
    this.onopen?.({} as Event)
  }

  receive(value: unknown) {
    this.onmessage?.({ data: JSON.stringify(value) } as MessageEvent)
  }

  send(data: string) {
    this.sent.push(data)
  }

  close() {
    this.readyState = 3
  }
}

describe('useEventsConnection project ownership', () => {
  it('keeps old socket callbacks bound to the stopped project controller', () => {
    const sockets: FakeWebSocket[] = []
    class InstalledWebSocket extends FakeWebSocket {
      constructor(_url: string) {
        super()
        sockets.push(this)
      }
    }
    vi.stubGlobal('WebSocket', InstalledWebSocket)
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const firstProject = vi.fn()
    const secondProject = vi.fn()
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    )
    const rendered = renderHook(({ projectId, callback }) => useEventsConnection(projectId, callback), {
      wrapper,
      initialProps: { projectId: 'project-1', callback: firstProject },
    })
    const oldSocket = sockets[0]

    rendered.rerender({ projectId: 'project-2', callback: secondProject })
    act(() => {
      oldSocket.receive({
        jsonrpc: '2.0',
        method: 'event.domain',
        params: { event: { type: 'com.mohist.issue.completed', projectId: 'project-1' } },
      })
    })

    expect(firstProject).not.toHaveBeenCalled()
    expect(secondProject).not.toHaveBeenCalled()
  })
})
