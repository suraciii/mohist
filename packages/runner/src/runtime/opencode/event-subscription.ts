/**
 * One `client.global.event()` subscription, routed by Session ID +
 * working directory. This module is a thin seam — it owns a single
 * async-iterable consumer and fans each normalized event out to its
 * listeners. It deliberately does not decide which events map to
 * which Mohist transcript/tool/usage/model/status facts; that lives
 * in higher-level code (T-004 turns / T-005 session commands).
 *
 * Per spec `specs/opencode-runtime/spec.md` "One global event
 * subscription" and `specs/opencode-turn-execution/spec.md` "idempotent
 * projection".
 */

import type { OpencodeClient } from "@opencode-ai/sdk/v2"

export interface RuntimeGlobalEvent {
  readonly type: string
  readonly sessionID?: string
  readonly directory?: string
  readonly payload?: Record<string, unknown>
}

export type RuntimeEventListener = (event: RuntimeGlobalEvent) => void

export interface EventSubscriptionOptions {
  readonly reconnectDelayMs?: number
}

export interface RuntimeEventSubscription {
  subscribe(listener: RuntimeEventListener): () => void
  close(): Promise<void>
}

export type EventSubscriptionFactory = (client: OpencodeClient) => RuntimeEventSubscription

export function createEventSubscription(
  client: OpencodeClient,
  options: EventSubscriptionOptions = {},
): RuntimeEventSubscription {
  const listeners = new Set<RuntimeEventListener>()
  let closed = false
  let pump: Promise<void> | null = null
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null

  const scheduleReconnect = () => {
    if (closed || listeners.size === 0 || reconnectTimer !== null) return
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null
      start()
    }, options.reconnectDelayMs ?? 1000)
    reconnectTimer.unref?.()
  }

  const start = () => {
    if (pump !== null || reconnectTimer !== null || closed || listeners.size === 0) return
    pump = (async () => {
      try {
        const result = await client.global.event({ throwOnError: true })
        const stream = result.stream
        for await (const envelope of stream as AsyncIterable<{
          directory?: string
          payload?: { type?: string; properties?: Record<string, unknown> }
        }>) {
          if (closed) break
          const payload = envelope?.payload
          if (!payload || typeof payload.type !== "string") continue
          const props = (payload.properties ?? {}) as Record<string, unknown>
          const sessionID = typeof props["sessionID"] === "string" ? (props["sessionID"] as string) : undefined
          const directory = typeof envelope.directory === "string" ? envelope.directory : undefined
          const event: RuntimeGlobalEvent = {
            type: payload.type,
            sessionID,
            directory,
            payload: props,
          }
          for (const listener of [...listeners]) {
            try {
              listener(event)
            } catch (error) {
              console.error("runtime event listener failed", error)
            }
          }
        }
      } catch (error) {
        if (!closed) {
          console.error("runtime event subscription terminated", error)
        }
      } finally {
        pump = null
        scheduleReconnect()
      }
    })()
  }

  return {
    subscribe(listener) {
      if (closed) return () => {}
      listeners.add(listener)
      start()
      return () => {
        listeners.delete(listener)
        if (listeners.size === 0 && reconnectTimer !== null) {
          clearTimeout(reconnectTimer)
          reconnectTimer = null
        }
      }
    },
    async close() {
      closed = true
      listeners.clear()
      if (reconnectTimer !== null) {
        clearTimeout(reconnectTimer)
        reconnectTimer = null
      }
      const pending = pump
      pump = null
      if (pending) await pending.catch(() => {})
    },
  }
}
