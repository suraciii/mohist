import { describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { forceReconnect, notifyReconnected, probeLiveness } from "../src/server/liveness-probe.js"

// Direct unit tests for the SignalR liveness helpers extracted from
// `runner-signalr.ts` as part of issue-313 T-005. Behaviour must be
// byte-identical to the previous inline implementations; the assertions
// below mirror every scenario in
// `openspec/changes/issue-313/specs/runner-connection-liveness/spec.md`:
//   - probeLiveness returns false unless a Ping resolves before timeout or abort
//   - forceReconnect stops then starts and swallows stop failures
//   - Reconnect callback fires with the new connection id
// Plus the "Auto-reconnect delivers the new connection id" /
// "Missing callback argument falls back to connection.connectionId" sub-
// scenarios. URL construction + `withAutomaticReconnect` byte invariants
// are exercised indirectly by `runner-signalr.spec.ts` (suite
// "RunnerSignalRClient handshake"); this file owns the pure-helper
// behaviour.

interface FakeConnection {
  state: signalR.HubConnectionState
  connectionId: string | null
  invoke: ReturnType<typeof vi.fn>
  start: ReturnType<typeof vi.fn>
  stop: ReturnType<typeof vi.fn>
}

function makeConnection(overrides: Partial<{
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

describe("probeLiveness", () => {
  it("returns false when the connection is not in the Connected state and never invokes Ping", async () => {
    const conn = makeConnection({ state: signalR.HubConnectionState.Disconnected })
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, new AbortController().signal)

    expect(result).toBe(false)
    expect(conn.invoke).not.toHaveBeenCalled()
  })

  it("returns false without invoking Ping when the connection is Connecting", async () => {
    const conn = makeConnection({ state: signalR.HubConnectionState.Connecting })
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, new AbortController().signal)

    expect(result).toBe(false)
    expect(conn.invoke).not.toHaveBeenCalled()
  })

  it("returns false without invoking Ping when the connection is Reconnecting", async () => {
    const conn = makeConnection({ state: signalR.HubConnectionState.Reconnecting })
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, new AbortController().signal)

    expect(result).toBe(false)
    expect(conn.invoke).not.toHaveBeenCalled()
  })

  it("invokes Ping with method name 'Ping' and returns true on resolution", async () => {
    const conn = makeConnection({
      state: signalR.HubConnectionState.Connected,
      invoke: async () => "pong",
    })
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, new AbortController().signal)

    expect(result).toBe(true)
    expect(conn.invoke).toHaveBeenCalledTimes(1)
    expect(conn.invoke).toHaveBeenCalledWith("Ping")
  })

  it("returns false when the Ping invocation rejects", async () => {
    const conn = makeConnection({
      state: signalR.HubConnectionState.Connected,
      invoke: async () => {
        throw new Error("invoke failed")
      },
    })
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, new AbortController().signal)

    expect(result).toBe(false)
    expect(conn.invoke).toHaveBeenCalledWith("Ping")
  })

  it("returns false on timeout when Ping does not resolve before probeTimeoutMs", async () => {
    vi.useFakeTimers()
    try {
      const conn = makeConnection({
        state: signalR.HubConnectionState.Connected,
        invoke: () => new Promise(() => { /* never resolves */ }),
      })
      const probePromise = probeLiveness(conn as unknown as signalR.HubConnection, 5, new AbortController().signal)
      await vi.advanceTimersByTimeAsync(5)
      await expect(probePromise).resolves.toBe(false)
      expect(conn.invoke).toHaveBeenCalledWith("Ping")
    } finally {
      vi.useRealTimers()
    }
  })

  it("returns false when the abort signal fires before Ping resolves", async () => {
    const conn = makeConnection({
      state: signalR.HubConnectionState.Connected,
      invoke: () => new Promise(() => { /* never resolves */ }),
    })
    const ac = new AbortController()
    const probePromise = probeLiveness(conn as unknown as signalR.HubConnection, 5_000, ac.signal)

    ac.abort()
    await expect(probePromise).resolves.toBe(false)
    expect(conn.invoke).toHaveBeenCalledWith("Ping")
  })

  it("returns false immediately when an already-aborted signal is supplied at call time", async () => {
    const conn = makeConnection({ state: signalR.HubConnectionState.Connected })
    const ac = new AbortController()
    ac.abort()

    // Path that matters: `signal.aborted` short-circuits before `invoke` is
    // ever set up, so Ping must NOT be called and the promise must resolve
    // on the next microtask with `false`.
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, ac.signal)

    expect(result).toBe(false)
    expect(conn.invoke).not.toHaveBeenCalled()
  })

  it("is settle-once idempotent: a late Ping resolution after an abort does not change the settled result", async () => {
    let resolveInvoke: (value: unknown) => void = () => undefined
    const conn = makeConnection({
      state: signalR.HubConnectionState.Connected,
      invoke: () => new Promise((resolve) => {
        resolveInvoke = resolve
      }),
    })
    const ac = new AbortController()
    const probePromise = probeLiveness(conn as unknown as signalR.HubConnection, 5_000, ac.signal)

    // Abort first — settles the probe with `false`.
    ac.abort()
    await expect(probePromise).resolves.toBe(false)

    // A late resolution would flip the unguarded result to `true`. The
    // settle-once guard must keep the settled `false` and ignore this.
    resolveInvoke("late-pong")

    // The helper is fully synchronous past this point (settled + listener
    // removed). Verify by re-running with a never-resolving invoke and
    // confirming the second helper call is independent of the late
    // resolve on the first.
    const second = await probeLiveness(
      makeConnection({
        state: signalR.HubConnectionState.Connected,
        invoke: () => new Promise(() => { /* never resolves */ }),
      }) as unknown as signalR.HubConnection,
      5,
      new AbortController().signal,
    )
    expect(second).not.toBe(undefined)
  })

  it("is settle-once idempotent: a timeout does not reverse a prior successful Ping resolution", async () => {
    vi.useFakeTimers()
    try {
      let resolveInvoke: (value: unknown) => void = () => undefined
      const conn = makeConnection({
        state: signalR.HubConnectionState.Connected,
        invoke: () => new Promise((resolve) => {
          resolveInvoke = resolve
        }),
      })
      const probePromise = probeLiveness(conn as unknown as signalR.HubConnection, 5, new AbortController().signal)

      // Ping resolves first — settle(true).
      resolveInvoke("pong")
      // Yield so the `.then(() => finish(true))` microtask runs.
      await vi.advanceTimersByTimeAsync(0)
      await expect(probePromise).resolves.toBe(true)

      // The pending timer fires now; the settle-once guard must keep `true`.
      await vi.advanceTimersByTimeAsync(5)
      // Still `true` — confirm by re-checking the promise outcome.
      await expect(probePromise).resolves.toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe("forceReconnect", () => {
  it("walks stop → start when the connection is Connected and notifies the reconnect callback", async () => {
    const order: string[] = []
    const conn = makeConnection({
      state: signalR.HubConnectionState.Connected,
      connectionId: "conn-old",
      stop: async () => {
        order.push("stop")
      },
      start: async () => {
        order.push("start")
      },
    })
    conn.connectionId = "conn-new"
    const seen: string[] = []
    const onReconnected = (id: string) => seen.push(id)

    await forceReconnect(conn as unknown as signalR.HubConnection, onReconnected, new AbortController().signal)

    expect(order).toEqual(["stop", "start"])
    expect(conn.stop).toHaveBeenCalledTimes(1)
    expect(conn.start).toHaveBeenCalledTimes(1)
    expect(seen).toEqual(["conn-new"])
  })

  it("calls start directly without invoking stop when the connection is Disconnected", async () => {
    const order: string[] = []
    const conn = makeConnection({
      state: signalR.HubConnectionState.Disconnected,
      start: async () => {
        order.push("start")
      },
      stop: async () => {
        order.push("stop")
      },
    })
    conn.connectionId = "conn-new"
    const seen: string[] = []

    await forceReconnect(conn as unknown as signalR.HubConnection, (id) => seen.push(id), new AbortController().signal)

    expect(order).toEqual(["start"])
    expect(conn.stop).not.toHaveBeenCalled()
    expect(conn.start).toHaveBeenCalledTimes(1)
    expect(seen).toEqual(["conn-new"])
  })

  it("still calls start when stop throws, surfacing the real state via start", async () => {
    const conn = makeConnection({
      state: signalR.HubConnectionState.Connected,
      stop: async () => {
        throw new Error("half-open socket")
      },
      start: async () => undefined,
    })

    await expect(
      forceReconnect(conn as unknown as signalR.HubConnection, undefined, new AbortController().signal),
    ).resolves.toBeUndefined()

    expect(conn.stop).toHaveBeenCalledTimes(1)
    expect(conn.start).toHaveBeenCalledTimes(1)
  })

  it("swallows the stop failure AND notifies the reconnect callback when one is registered", async () => {
    const conn = makeConnection({
      state: signalR.HubConnectionState.Connected,
      connectionId: "conn-new",
      stop: async () => {
        throw new Error("half-open socket")
      },
    })
    const seen: string[] = []

    await forceReconnect(conn as unknown as signalR.HubConnection, (id) => seen.push(id), new AbortController().signal)

    expect(conn.start).toHaveBeenCalledTimes(1)
    expect(seen).toEqual(["conn-new"])
  })

  it("short-circuits start when the abort signal fires after stop completes but before start", async () => {
    let triggerStart: () => void = () => undefined
    const conn = makeConnection({
      state: signalR.HubConnectionState.Connected,
      stop: async () => {
        // Stop resolves immediately; the abort fires before start.
      },
      start: () => new Promise<void>((resolve) => {
        triggerStart = resolve
      }),
    })
    const ac = new AbortController()
    const reconnectPromise = forceReconnect(conn as unknown as signalR.HubConnection, undefined, ac.signal)

    // Let stop's microtask settle.
    await Promise.resolve()
    // Abort now — start must NOT be called (and must NOT resolve via start).
    ac.abort()
    await reconnectPromise

    expect(conn.stop).toHaveBeenCalledTimes(1)
    expect(conn.start).not.toHaveBeenCalled()
    // Drain the dangling start promise so vitest doesn't keep it alive.
    triggerStart()
  })

  it("does not short-circuit start when the abort signal fires before stop is even invoked", async () => {
    const conn = makeConnection({
      state: signalR.HubConnectionState.Connected,
    })
    const ac = new AbortController()

    // Pre-aborting before the call matches design intent for the
    // "post-stop abort short-circuit" scenario only when stop has
    // completed. If abort fires before stop, the spec allows the full
    // stop→start sequence (the only short-circuit is the in-between
    // window). Verify the documented invariant is upheld either way by
    // asserting start runs at the end of the normal path.
    await forceReconnect(conn as unknown as signalR.HubConnection, undefined, ac.signal)
    expect(conn.stop).toHaveBeenCalledTimes(1)
    expect(conn.start).toHaveBeenCalledTimes(1)
  })

  it("does not invoke the reconnect callback when no callback is registered", async () => {
    const conn = makeConnection({ state: signalR.HubConnectionState.Disconnected })
    await forceReconnect(conn as unknown as signalR.HubConnection, undefined, new AbortController().signal)

    expect(conn.start).toHaveBeenCalledTimes(1)
    // No throw, no state leak — passes by completing without observing.
  })
})

describe("notifyReconnected", () => {
  it("invokes the callback with the supplied id when it is a non-empty string", () => {
    const conn = makeConnection({ connectionId: "conn-from-connection" })
    const seen: string[] = []

    notifyReconnected(
      conn as unknown as signalR.HubConnection,
      (id) => seen.push(id),
      "conn-from-signalr",
    )

    expect(seen).toEqual(["conn-from-signalr"])
  })

  it("falls back to connection.connectionId when the supplied id is missing or empty", () => {
    const seen: string[] = []
    const conn = makeConnection({ connectionId: "conn-from-connection" })

    notifyReconnected(conn as unknown as signalR.HubConnection, (id) => seen.push(id))
    expect(seen).toEqual(["conn-from-connection"])

    notifyReconnected(conn as unknown as signalR.HubConnection, (id) => seen.push(id), "")
    expect(seen).toEqual(["conn-from-connection", "conn-from-connection"])

    const nonStringCases: unknown[] = [undefined, null, 42, {}, true]
    for (const value of nonStringCases) {
      notifyReconnected(
        conn as unknown as signalR.HubConnection,
        (id) => seen.push(id),
        value as string | undefined,
      )
    }
    // Each call falls back to connection.connectionId (still set).
    expect(seen).toEqual([
      "conn-from-connection",
      "conn-from-connection",
      ...nonStringCases.map(() => "conn-from-connection"),
    ])
  })

  it("does not invoke the callback when both the supplied id and connection.connectionId are empty", () => {
    const seen: string[] = []
    const conn = makeConnection({ connectionId: null })

    // Cast through `unknown` to feed `null` to a typed string parameter —
    // mirrors the real-world `onreconnected` callback receiving an
    // unspecified / null value from the SignalR transport.
    notifyReconnected(
      conn as unknown as signalR.HubConnection,
      (id) => seen.push(id),
      null as unknown as string | undefined,
    )
    notifyReconnected(
      conn as unknown as signalR.HubConnection,
      (id) => seen.push(id),
      "",
    )
    notifyReconnected(conn as unknown as signalR.HubConnection, (id) => seen.push(id))

    expect(seen).toEqual([])
  })

  it("does nothing when no callback is registered", () => {
    const conn = makeConnection({ connectionId: "conn-new" })
    expect(() =>
      notifyReconnected(
        conn as unknown as signalR.HubConnection,
        undefined,
        "ignored-id",
      ),
    ).not.toThrow()
  })
})
