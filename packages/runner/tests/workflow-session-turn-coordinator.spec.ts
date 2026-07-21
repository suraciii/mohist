import { describe, expect, it } from "vitest"
import { WorkflowSessionTurnCoordinator } from "../src/runtime/workflow-session-turn-coordinator.js"
import { workflowSessionName } from "../src/actions/workflow-session-name.js"
import { deferred } from "./support/deferred.js"

const key = (sessionName: string) => ({
  projectId: "project-1",
  workflowRunId: "workflow-1",
  sessionName,
})

describe("WorkflowSessionTurnCoordinator", () => {
  it("serializes task cleanup and a check across runtime changes", async () => {
    const coordinator = new WorkflowSessionTurnCoordinator()
    const firstAction = deferred<void>()
    const cleanup = deferred<void>()
    const events: string[] = []

    const task = coordinator.withTurn(key("plan"), async () => {
      events.push("opencode-action")
      await firstAction.promise
      events.push("opencode-cleanup")
      await cleanup.promise
    })
    const check = coordinator.withTurn(key("plan"), async () => { events.push("pi-check") })

    await Promise.resolve()
    await Promise.resolve()
    expect(events).toEqual(["opencode-action"])
    firstAction.resolve()
    await Promise.resolve()
    expect(events).toEqual(["opencode-action", "opencode-cleanup"])
    cleanup.resolve()
    await Promise.all([task, check])
    expect(events).toEqual(["opencode-action", "opencode-cleanup", "pi-check"])
  })

  it("runs different logical Sessions concurrently", async () => {
    const coordinator = new WorkflowSessionTurnCoordinator()
    const release = deferred<void>()
    const started: string[] = []
    const first = coordinator.withTurn(key("first"), async () => {
      started.push("first")
      await release.promise
    })
    const second = coordinator.withTurn(key("second"), async () => { started.push("second") })

    await second
    expect(started).toEqual(["first", "second"])
    release.resolve()
    await first
  })

  it("removes settled tails and does not poison later work after rejection", async () => {
    const coordinator = new WorkflowSessionTurnCoordinator()
    const failure = coordinator.withTurn(key("failure"), async () => { throw new Error("turn failed") })
    await expect(failure).rejects.toThrow("turn failed")
    expect(coordinator.sizeForTest()).toBe(0)

    await expect(coordinator.withTurn(key("failure"), async () => "recovered")).resolves.toBe("recovered")
    expect(coordinator.sizeForTest()).toBe(0)
  })

  it("uses the Work ID for omitted or blank Session names", () => {
    expect(workflowSessionName({}, "work-1")).toBe("work-1")
    expect(workflowSessionName({ session: null }, "work-2")).toBe("work-2")
    expect(workflowSessionName({ session: "  " }, "work-3")).toBe("work-3")
    expect(workflowSessionName({ session: "  plan  " }, "work-4")).toBe("plan")
  })
})
