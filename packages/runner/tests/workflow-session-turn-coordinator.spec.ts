import { describe, expect, it } from "vitest"
import { executeCheckDispatch } from "../src/runtime/check-execution.js"
import { WorkflowSessionTurnCoordinator } from "../src/runtime/workflow-session-turn-coordinator.js"
import { workflowSessionName } from "../src/actions/workflow-session-name.js"
import { ActionRegistry } from "../src/actions/registry.js"
import { defineAction } from "../src/actions/define-action.js"
import type { ActionDefinition } from "../src/actions/manifest.js"
import { succeed } from "../src/actions/action-result.js"
import type { ActionInvocationContext } from "../src/actions/context.js"
import { deferred } from "./support/deferred.js"

const key = (sessionName: string) => ({
  projectId: "project-1",
  workflowRunId: "workflow-1",
  sessionName,
})

const AGENT_MANIFEST = {
  name: "mohist/opencode",
  inputs: {
    prompt: { types: ["string"] as const, required: true as const },
    session: { types: ["string"] as const },
  },
  outputs: [{ name: "promise" }],
  errors: [{ code: "turn-failed" }],
}

const PI_MANIFEST = {
  name: "mohist/pi",
  inputs: {
    prompt: { types: ["string"] as const, required: true as const },
    session: { types: ["string"] as const },
  },
  outputs: [{ name: "promise" }],
  errors: [{ code: "turn-failed" }],
}

function makeRegistry(action: (context: ActionInvocationContext) => ReturnType<typeof succeed>): ActionRegistry {
  const definitions: ActionDefinition[] = [
    defineAction({ manifest: AGENT_MANIFEST, run: action }),
    defineAction({ manifest: PI_MANIFEST, run: action }),
  ]
  return new ActionRegistry(definitions)
}

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

describe("check Action turn coordination", () => {
  it("serializes same-name checks while leaving different names independent", async () => {
    const coordinator = new WorkflowSessionTurnCoordinator()
    const release = deferred<void>()
    const started: string[] = []
    const action = async (context: ActionInvocationContext) => {
      const session = String(context.with?.session ?? context.workId).trim()
      started.push(session)
      if (session === "shared" && started.filter((value) => value === "shared").length === 1) await release.promise
      return succeed("ok")
    }
    const actions = makeRegistry(action)
    const context = {
      workflowRunId: "workflow-1",
      workId: "check-work",
      workType: "checks",
      projectId: "project-1",
      signal: new AbortController().signal,
      writeVars: async () => {},
    } as const
    const shared = executeCheckDispatch(
      [{ name: "task", uses: "mohist/opencode", with: { prompt: "hello", session: " shared " } }],
      {},
      { actions, context, coordinator, formatUnresolved: () => "unresolved", resolveWorkDir: async () => "/work", toCheckStatus: (status) => status },
    )
    const sharedAgain = executeCheckDispatch(
      [{ name: "retry", uses: "mohist/pi", with: { prompt: "hello", session: "shared" } }],
      {},
      { actions, context, coordinator, formatUnresolved: () => "unresolved", resolveWorkDir: async () => "/work", toCheckStatus: (status) => status },
    )
    const other = executeCheckDispatch(
      [{ name: "other", uses: "mohist/pi", with: { prompt: "hello", session: "other" } }],
      {},
      { actions, context, coordinator, formatUnresolved: () => "unresolved", resolveWorkDir: async () => "/work", toCheckStatus: (status) => status },
    )

    await other
    expect(started).toEqual(["shared", "other"])
    release.resolve()
    await Promise.all([shared, sharedAgain])
    expect(started).toEqual(["shared", "other", "shared"])
  })
})
