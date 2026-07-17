import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, describe, expect, it, vi } from "vitest"
import { FollowupFailureOutbox } from "../src/server/followup-failure-outbox.js"

const roots: string[] = []

afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })))
})

describe("FollowupFailureOutbox", () => {
  it("retains a failed terminal delivery across restart until the server accepts it", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-followup-outbox-"))
    roots.push(root)
    const agentSessionRuntimeEvents = vi.fn()
      .mockRejectedValueOnce(new Error("server unavailable"))
      .mockResolvedValueOnce(undefined)
    const server = {
      workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
      agentSessionRuntimeEvents,
    }

    const first = new FollowupFailureOutbox(root)
    await first.load()
    await first.record({
      operationId: "followup-1",
      target: { kind: "generic", projectId: "project-1", sessionId: "session-1" },
      runtimeSessionId: "runtime-1",
      status: "failed",
      error: "prompt rejected",
      completedAt: "2026-01-01T00:00:00.000Z",
    }, server as never)

    expect(agentSessionRuntimeEvents).toHaveBeenCalledTimes(1)

    const restarted = new FollowupFailureOutbox(root)
    await restarted.load()
    await restarted.drain(server as never)

    expect(agentSessionRuntimeEvents).toHaveBeenCalledTimes(2)
    expect(agentSessionRuntimeEvents).toHaveBeenLastCalledWith(
      "project-1",
      "session-1",
      expect.objectContaining({
        runtimeEvents: [expect.objectContaining({
          type: "session.followup_failed",
          payload: expect.objectContaining({ operationId: "followup-1" }),
        })],
      }),
      expect.any(AbortSignal),
    )
  })

  it("serializes concurrent failures so restart delivery retains every operation", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-followup-outbox-"))
    roots.push(root)
    const unavailable = {
      workflowAgentSessionRuntimeEvents: vi.fn(async () => { throw new Error("server unavailable") }),
      agentSessionRuntimeEvents: vi.fn(async () => { throw new Error("server unavailable") }),
    }
    const outbox = new FollowupFailureOutbox(root)
    await outbox.load()

    await Promise.all(["followup-1", "followup-2"].map((operationId) => outbox.record({
      operationId,
      target: { kind: "generic", projectId: "project-1", sessionId: "session-1" },
      runtimeSessionId: "runtime-1",
      status: "failed",
      error: "prompt rejected",
      completedAt: "2026-01-01T00:00:00.000Z",
    }, unavailable as never)))

    const accepted = {
      workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
      agentSessionRuntimeEvents: vi.fn(async () => undefined),
    }
    const restarted = new FollowupFailureOutbox(root)
    await restarted.load()
    await restarted.drain(accepted as never)

    const calls = accepted.agentSessionRuntimeEvents.mock.calls as unknown as Array<[
      string,
      string,
      { runtimeEvents: Array<{ payload: { operationId: string } }> },
      AbortSignal,
    ]>
    const delivered = calls.map(([, , body]) => body.runtimeEvents[0]!.payload.operationId).sort()
    expect(delivered).toEqual(["followup-1", "followup-2"])
  })

  it("retries a terminal delivery after its request stalls", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-followup-outbox-"))
    roots.push(root)
    vi.useFakeTimers()
    let entered!: () => void
    const enteredDelivery = new Promise<void>((resolve) => { entered = resolve })
    const server = {
      workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
      agentSessionRuntimeEvents: vi.fn()
        .mockImplementationOnce((...args: unknown[]) => new Promise<void>((_, reject) => {
          const signal = args[3] as AbortSignal
          entered()
          signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true })
        }))
        .mockResolvedValueOnce(undefined),
    }
    const outbox = new FollowupFailureOutbox(root)
    await outbox.load()

    const recording = outbox.record({
      operationId: "followup-stalled",
      target: { kind: "generic", projectId: "project-1", sessionId: "session-1" },
      runtimeSessionId: "runtime-1",
      status: "failed",
      error: "prompt rejected",
      completedAt: "2026-01-01T00:00:00.000Z",
    }, server as never)
    await enteredDelivery
    await vi.advanceTimersByTimeAsync(5_000)
    await recording
    await vi.advanceTimersByTimeAsync(2_000)

    expect(server.agentSessionRuntimeEvents).toHaveBeenCalledTimes(2)
    vi.useRealTimers()
  })
})
