import { afterEach, describe, expect, it, vi } from "vitest"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import { createEventSubscription } from "../src/runtime/opencode/event-subscription.js"

afterEach(() => {
  vi.useRealTimers()
})

describe("OpenCode global event subscription", () => {
  it("reconnects after the stream ends while listeners remain", async () => {
    vi.useFakeTimers()
    let generation = 0
    const event = vi.fn(async () => {
      generation += 1
      const current = generation
      return {
        stream: (async function* () {
          yield {
            directory: "/tmp/project-a",
            payload: {
              type: "server.connected",
              properties: { generation: current },
            },
          }
        })(),
      }
    })
    const client = { global: { event } } as unknown as OpencodeClient
    const subscription = createEventSubscription(client, { reconnectDelayMs: 25 })
    const seen: Array<{ generation: number; directory: string | undefined }> = []
    subscription.subscribe((received) => {
      seen.push({
        generation: received.payload?.["generation"] as number,
        directory: received.directory,
      })
    })

    await vi.advanceTimersByTimeAsync(0)
    expect(seen).toEqual([{ generation: 1, directory: "/tmp/project-a" }])
    expect(event).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(25)
    expect(seen).toEqual([
      { generation: 1, directory: "/tmp/project-a" },
      { generation: 2, directory: "/tmp/project-a" },
    ])
    expect(event).toHaveBeenCalledTimes(2)
    expect(event).toHaveBeenNthCalledWith(1, expect.objectContaining({ throwOnError: true, signal: expect.any(AbortSignal) }))
    expect(event).toHaveBeenNthCalledWith(2, expect.objectContaining({ throwOnError: true, signal: expect.any(AbortSignal) }))

    await subscription.close()
  })

  it("aborts the active event stream when closed", async () => {
    let requestSignal: AbortSignal | undefined
    let markStarted: () => void = () => {}
    let releaseStream: () => void = () => {}
    const started = new Promise<void>((resolve) => { markStarted = resolve })
    const release = new Promise<void>((resolve) => { releaseStream = resolve })
    const event = vi.fn(async (options: { signal: AbortSignal }) => {
      requestSignal = options.signal
      return {
        stream: (async function* () {
          markStarted()
          await Promise.race([
            release,
            new Promise<void>((resolve) => options.signal.addEventListener("abort", () => resolve(), { once: true })),
          ])
        })(),
      }
    })
    const client = { global: { event } } as unknown as OpencodeClient
    const subscription = createEventSubscription(client)
    subscription.subscribe(() => {})
    await started

    const closing = subscription.close()
    try {
      expect(requestSignal?.aborted).toBe(true)
    } finally {
      releaseStream()
      await closing
    }
  })
})
