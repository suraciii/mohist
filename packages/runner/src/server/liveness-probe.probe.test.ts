import { describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { makeLivenessConnection } from "../../tests/support/liveness-probe-test-utils.js"
import { probeLiveness } from "./liveness-probe.js"

describe("probeLiveness", () => {
  it("returns false when the connection is not in the Connected state and never invokes Ping", async () => {
    const conn = makeLivenessConnection({ state: signalR.HubConnectionState.Disconnected })
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, new AbortController().signal)

    expect(result).toBe(false)
    expect(conn.invoke).not.toHaveBeenCalled()
  })

  it("returns false without invoking Ping when the connection is Connecting", async () => {
    const conn = makeLivenessConnection({ state: signalR.HubConnectionState.Connecting })
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, new AbortController().signal)

    expect(result).toBe(false)
    expect(conn.invoke).not.toHaveBeenCalled()
  })

  it("returns false without invoking Ping when the connection is Reconnecting", async () => {
    const conn = makeLivenessConnection({ state: signalR.HubConnectionState.Reconnecting })
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, new AbortController().signal)

    expect(result).toBe(false)
    expect(conn.invoke).not.toHaveBeenCalled()
  })

  it("invokes Ping with method name 'Ping' and returns true on resolution", async () => {
    const conn = makeLivenessConnection({
      state: signalR.HubConnectionState.Connected,
      invoke: async () => "pong",
    })
    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, new AbortController().signal)

    expect(result).toBe(true)
    expect(conn.invoke).toHaveBeenCalledTimes(1)
    expect(conn.invoke).toHaveBeenCalledWith("Ping")
  })

  it("returns false when the Ping invocation rejects", async () => {
    const conn = makeLivenessConnection({
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
      const conn = makeLivenessConnection({
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
    const conn = makeLivenessConnection({
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
    const conn = makeLivenessConnection({ state: signalR.HubConnectionState.Connected })
    const ac = new AbortController()
    ac.abort()

    const result = await probeLiveness(conn as unknown as signalR.HubConnection, 5_000, ac.signal)

    expect(result).toBe(false)
    expect(conn.invoke).not.toHaveBeenCalled()
  })

  it("is settle-once idempotent: a late Ping resolution after an abort does not change the settled result", async () => {
    vi.useFakeTimers()
    try {
      let resolveInvoke: (value: unknown) => void = () => undefined
      const conn = makeLivenessConnection({
        state: signalR.HubConnectionState.Connected,
        invoke: () => new Promise((resolve) => {
          resolveInvoke = resolve
        }),
      })
      const ac = new AbortController()
      const probePromise = probeLiveness(conn as unknown as signalR.HubConnection, 5_000, ac.signal)

      ac.abort()
      await expect(probePromise).resolves.toBe(false)

      resolveInvoke("late-pong")
      await vi.advanceTimersByTimeAsync(0)
      await expect(probePromise).resolves.toBe(false)

      const secondProbe = probeLiveness(
        makeLivenessConnection({
          state: signalR.HubConnectionState.Connected,
          invoke: () => new Promise(() => { /* never resolves */ }),
        }) as unknown as signalR.HubConnection,
        5,
        new AbortController().signal,
      )
      await vi.advanceTimersByTimeAsync(5)
      await expect(secondProbe).resolves.toBe(false)
    } finally {
      vi.useRealTimers()
    }
  })

  it("is settle-once idempotent: a timeout does not reverse a prior successful Ping resolution", async () => {
    vi.useFakeTimers()
    try {
      let resolveInvoke: (value: unknown) => void = () => undefined
      const conn = makeLivenessConnection({
        state: signalR.HubConnectionState.Connected,
        invoke: () => new Promise((resolve) => {
          resolveInvoke = resolve
        }),
      })
      const probePromise = probeLiveness(conn as unknown as signalR.HubConnection, 5, new AbortController().signal)

      resolveInvoke("pong")
      await vi.advanceTimersByTimeAsync(0)
      await expect(probePromise).resolves.toBe(true)

      await vi.advanceTimersByTimeAsync(5)
      await expect(probePromise).resolves.toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })
})
