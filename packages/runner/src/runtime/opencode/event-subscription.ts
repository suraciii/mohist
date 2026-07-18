/**
 * One `client.global.event()` subscription, routed by Session ID +
 * working directory. This module is a thin seam — it owns a single
 * async-iterable consumer and a fan-out map keyed by (sessionId,
 * directory). It deliberately does not decide which events map to
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

export interface RuntimeEventSubscription {
  subscribe(listener: RuntimeEventListener): () => void
  close(): Promise<void>
}

export type EventSubscriptionFactory = (client: OpencodeClient) => RuntimeEventSubscription

export function createEventSubscription(client: OpencodeClient): RuntimeEventSubscription {
  const listeners = new Set<RuntimeEventListener>()
  let closed = false
  let pump: Promise<void> | null = null

  const start = () => {
    if (pump !== null) return
    pump = (async () => {
      try {
        const result = await client.global.event()
        const stream = result.stream
        for await (const envelope of stream as AsyncIterable<{ payload?: { type?: string; properties?: Record<string, unknown> } }>) {
          if (closed) break
          const payload = envelope?.payload
          if (!payload || typeof payload.type !== "string") continue
          const props = (payload.properties ?? {}) as Record<string, unknown>
          const sessionID = typeof props["sessionID"] === "string" ? (props["sessionID"] as string) : undefined
          const directory = typeof props["directory"] === "string" ? (props["directory"] as string) : undefined
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
      }
    },
    async close() {
      closed = true
      listeners.clear()
      const pending = pump
      pump = null
      if (pending) await pending.catch(() => {})
    },
  }
}
