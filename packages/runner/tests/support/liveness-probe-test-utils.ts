import * as signalR from "@microsoft/signalr"
import { vi } from "vitest"

interface FakeConnection {
  state: signalR.HubConnectionState
  connectionId: string | null
  invoke: ReturnType<typeof vi.fn>
  start: ReturnType<typeof vi.fn>
  stop: ReturnType<typeof vi.fn>
}

export function makeLivenessConnection(overrides: Partial<{
  state: signalR.HubConnectionState
  connectionId: string | null
  invoke: (arg?: string) => Promise<unknown>
  start: () => Promise<void>
  stop: () => Promise<void>
}> = {}): FakeConnection {
  return {
    state: overrides.state ?? signalR.HubConnectionState.Disconnected,
    connectionId: overrides.connectionId ?? null,
    invoke: vi.fn(overrides.invoke ?? (async () => "pong")),
    start: vi.fn(overrides.start ?? (async () => undefined)),
    stop: vi.fn(overrides.stop ?? (async () => undefined)),
  } satisfies FakeConnection
}
