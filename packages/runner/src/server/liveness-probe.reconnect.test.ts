import { describe, expect, it } from "vitest"
import * as signalR from "@microsoft/signalr"
import { makeLivenessConnection } from "../../tests/support/liveness-probe-test-utils.js"
import { forceReconnect, notifyReconnected } from "./liveness-probe.js"

describe("forceReconnect", () => {
  it("walks stop → start when the connection is Connected and notifies the reconnect callback", async () => {
    const order: string[] = []
    const conn = makeLivenessConnection({
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
    const conn = makeLivenessConnection({
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
    const conn = makeLivenessConnection({
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
    const conn = makeLivenessConnection({
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
    const ac = new AbortController()
    const conn = makeLivenessConnection({
      state: signalR.HubConnectionState.Connected,
      stop: async () => {
        queueMicrotask(() => ac.abort())
      },
    })

    await forceReconnect(conn as unknown as signalR.HubConnection, undefined, ac.signal)

    expect(conn.stop).toHaveBeenCalledTimes(1)
    expect(conn.start).not.toHaveBeenCalled()
  })

  it("starts after stop when the abort signal remains active", async () => {
    const conn = makeLivenessConnection({
      state: signalR.HubConnectionState.Connected,
    })
    const ac = new AbortController()

    await forceReconnect(conn as unknown as signalR.HubConnection, undefined, ac.signal)
    expect(conn.stop).toHaveBeenCalledTimes(1)
    expect(conn.start).toHaveBeenCalledTimes(1)
  })

  it("does not invoke the reconnect callback when no callback is registered", async () => {
    const conn = makeLivenessConnection({ state: signalR.HubConnectionState.Disconnected })
    await forceReconnect(conn as unknown as signalR.HubConnection, undefined, new AbortController().signal)

    expect(conn.start).toHaveBeenCalledTimes(1)
  })
})

describe("notifyReconnected", () => {
  it("invokes the callback with the supplied id when it is a non-empty string", () => {
    const conn = makeLivenessConnection({ connectionId: "conn-from-connection" })
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
    const conn = makeLivenessConnection({ connectionId: "conn-from-connection" })

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
    expect(seen).toEqual([
      "conn-from-connection",
      "conn-from-connection",
      ...nonStringCases.map(() => "conn-from-connection"),
    ])
  })

  it("does not invoke the callback when both the supplied id and connection.connectionId are empty", () => {
    const seen: string[] = []
    const conn = makeLivenessConnection({ connectionId: null })

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
    const conn = makeLivenessConnection({ connectionId: "conn-new" })
    expect(() =>
      notifyReconnected(
        conn as unknown as signalR.HubConnection,
        undefined,
        "ignored-id",
      ),
    ).not.toThrow()
  })
})
