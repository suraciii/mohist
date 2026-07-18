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
    expect(event).toHaveBeenNthCalledWith(1, { throwOnError: true })
    expect(event).toHaveBeenNthCalledWith(2, { throwOnError: true })

    await subscription.close()
  })
})
