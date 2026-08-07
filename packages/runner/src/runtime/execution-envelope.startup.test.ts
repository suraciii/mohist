import { describe, expect, it } from "vitest"
import { buildExecutionEnvelope } from "./execution-envelope.js"

describe("agent session startup envelope", () => {
  it("places the immutable startup snapshot before the task prompt", () => {
    const envelope = buildExecutionEnvelope("task prompt", null, [], null, {
      projectId: "proj_1",
      sessionId: "sess_1",
      parentSessionId: "sess_parent",
      allowedSubagents: [{
        agentId: "agent_child",
        nameAtLaunch: "child",
        descriptionAtLaunch: "does child work",
      }],
      spawnCommand: "mo agent spawn <agent-ref> ...",
      workDir: "/inherited/agent-workspace",
      pinnedRunnerId: "runner_pinned",
      agentId: "agent_target",
      agentName: "target-agent",
    })

    expect(envelope).toMatch(
      /^\[mohist-agent-session-startup\][\s\S]*\[\/mohist-agent-session-startup\]\n\ntask prompt$/,
    )
    expect(envelope).not.toContain("mohist-system-facts")
    expect(envelope).toContain('"projectId":"proj_1"')
    expect(envelope).toContain('"sessionId":"sess_1"')
    expect(envelope).toContain('"parentSessionId":"sess_parent"')
    expect(envelope).toContain('"allowedSubagents":[{"agentId":"agent_child"')
    expect(envelope).toContain('"spawnCommand":"mo agent spawn <agent-ref> ..."')
    expect(envelope).toContain('"workDir":"/inherited/agent-workspace"')
    expect(envelope).toContain('"pinnedRunnerId":"runner_pinned"')
    expect(envelope).toContain('"agentId":"agent_target"')
    expect(envelope).toContain('"agentName":"target-agent"')
  })

  it("keeps startup ahead of the existing Slack and execution blocks", () => {
    const envelope = buildExecutionEnvelope("task prompt", "instructions", [], {
      version: 1,
      replyAnchor: {
        workspaceId: "W1",
        conversationId: "C1",
        threadRootMessageId: "M1",
        triggeringMessageId: "M2",
        initiatingMemberId: "U1",
        connectionId: "conn-1",
        sessionId: "sess_1",
        dispatchRef: "dispatch-1",
      },
      collaborationSkill: {
        name: "slack",
        version: "1",
        instructions: "collaborate",
        contentHash: "hash",
      },
    }, {
      projectId: "proj_1",
      sessionId: "sess_1",
      allowedSubagents: [],
      spawnCommand: "mo agent spawn",
    })

    expect(envelope.indexOf("[mohist-agent-session-startup]")).toBe(0)
    expect(envelope.indexOf("[mohist-system-facts]")).toBeGreaterThan(0)
    expect(envelope.indexOf("[mohist-execution-definition]")).toBeGreaterThan(envelope.indexOf("[mohist-system-facts]"))
    expect(envelope.endsWith("task prompt")).toBe(true)
  })

  it("places the workspace anchor at the very head of the envelope", () => {
    const envelope = buildExecutionEnvelope(
      "task prompt",
      null,
      [],
      null,
      null,
      "Working directory: /ws/pay. All workspace files live here — do not search $HOME. Repository checkouts belong under repos/; plans and research belong at the workspace root.",
    )

    expect(envelope.indexOf("[mohist-workspace-anchor]")).toBe(0)
    expect(envelope).toMatch(
      /^\[mohist-workspace-anchor\]\nWorking directory: \/ws\/pay[\s\S]*\[\/mohist-workspace-anchor\]\n\ntask prompt$/,
    )
    expect(envelope).toContain("do not search $HOME")
    expect(envelope).toContain("repos/")
  })

  it("emits no anchor block when the anchor is absent or blank", () => {
    expect(buildExecutionEnvelope("task prompt")).not.toContain("[mohist-workspace-anchor]")
    expect(buildExecutionEnvelope("task prompt", null, [], null, null, "   ")).not.toContain("[mohist-workspace-anchor]")
  })
})
