// SignalR connection liveness + reconnect helpers: probing (`Ping`)
// with a settle-once-idempotent timeout/abort race, manually tearing down
// and rebuilding the connection, and firing the `onReconnected` callback
// with the live connection id.

import * as signalR from "@microsoft/signalr"

export type OnReconnectedCallback = (connectionId: string) => void

/**
 * Probes the SignalR connection by invoking `Ping` and waiting for
 * `probeTimeoutMs` or `signal.abort` (whichever comes first). Returns
 * `false` immediately when the connection is not in the `Connected`
 * state, without touching the transport. Once settled (by Ping
 * resolution, rejection, timeout, or abort), all later events are
 * ignored — the probe never reverses a settled result.
 */
export async function probeLiveness(
  connection: signalR.HubConnection,
  probeTimeoutMs: number,
  signal: AbortSignal,
): Promise<boolean> {
  if (connection.state !== signalR.HubConnectionState.Connected) {
    return false
  }
  return await new Promise<boolean>((resolve) => {
    let settled = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const finish = (result: boolean) => {
      if (settled) return
      settled = true
      if (timer) clearTimeout(timer)
      if (signal) signal.removeEventListener("abort", onAbort)
      resolve(result)
    }
    const onAbort = () => finish(false)
    timer = setTimeout(() => finish(false), probeTimeoutMs)
    if (signal.aborted) {
      finish(false)
      return
    }
    signal.addEventListener("abort", onAbort, { once: true })
    connection
      .invoke("Ping")
      .then(() => finish(true))
      .catch(() => finish(false))
  })
}

/**
 * Tearing the connection down and rebuilding it. When the connection is
 * already `Disconnected` we go straight to `start` — `stop` would be a
 * no-op and would race with whatever put us into the `Disconnected`
 * state in the first place. Otherwise we walk `stop → start` and
 * deliberately swallow any stop error (a half-open socket may throw on
 * stop; the subsequent `start` is what surfaces the real state). If the
 * supplied `AbortSignal` fires after `stop` completes but before
 * `start`, `start` is skipped. After a successful start, `onReconnected`
 * (if any) is invoked via `notifyReconnected` with the live
 * `connection.connectionId`.
 */
export async function forceReconnect(
  connection: signalR.HubConnection,
  onReconnected: OnReconnectedCallback | undefined,
  signal: AbortSignal,
): Promise<void> {
  if (connection.state === signalR.HubConnectionState.Disconnected) {
    await connection.start()
    notifyReconnected(connection, onReconnected)
    return
  }
  try {
    await connection.stop()
  } catch {
    // best effort — a half-open socket may throw on stop; the start() below
    // will surface the real state.
  }
  if (signal.aborted) return
  await connection.start()
  notifyReconnected(connection, onReconnected)
}

/**
 * Invokes the `onReconnected` callback with the best connection id we
 * can produce. Prefers the id supplied by SignalR (the `onreconnected`
 * callback argument) when it is a non-empty string; otherwise falls
 * back to the live `connection.connectionId`. When neither yields a
 * non-empty id (e.g. the auto-reconnect completed before the id was
 * assigned, or the connection was torn down) the callback is left
 * dormant.
 */
export function notifyReconnected(
  connection: signalR.HubConnection,
  onReconnected: OnReconnectedCallback | undefined,
  connectionId?: string,
): void {
  if (!onReconnected) return
  const id = typeof connectionId === "string" && connectionId.length > 0
    ? connectionId
    : (connection.connectionId ?? "")
  if (id) onReconnected(id)
}
