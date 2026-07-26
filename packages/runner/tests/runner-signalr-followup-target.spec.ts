import { describe, expect, it } from "vitest"
import { resolveSessionTarget, type ReceiveFollowupPayload } from "../src/server/runner-signalr.js"

describe("resolveSessionTarget", () => {
  it("ResolvesTargetField", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "generic", projectId: "proj-1", sessionId: "gen-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toEqual({
      kind: "generic",
      projectId: "proj-1",
      sessionId: "gen-1",
    })
  })

  it("CarriesPersistedBinding_WhenPresent", () => {
    const payload: ReceiveFollowupPayload = {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId: "gen-1",
        binding: {
          runtime: "opencode",
          runtimeSessionId: "runtime-1",
          runnerId: "runner-1",
          workDir: "/work/project",
        },
      },
      text: "x",
    }

    expect(resolveSessionTarget(payload)).toEqual({
      kind: "generic",
      projectId: "proj-1",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-1",
        workDir: "/work/project",
      },
    })
  })

  it("ReturnsNull_WhenGenericTargetMissingSessionId", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "generic", projectId: "proj-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it("ReturnsNull_WhenWorkflowTargetMissingSessionName", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wr-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it("ReturnsNull_WhenNoTarget", () => {
    const payload: ReceiveFollowupPayload = { text: "x" }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it("ReturnsNull_OnUnknownTargetKind", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "weird" as unknown as "workflow", projectId: "proj-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })
})
