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
    })

    expect(envelope).toMatch(
      /^\[mohist-agent-session-startup\][\s\S]*\[\/mohist-agent-session-startup\]\n\ntask prompt$/,
    )
    expect(envelope).not.toContain("mohist-system-facts")
    expect(envelope).toContain('"projectId":"proj_1"')
    expect(envelope).toContain('"allowedSubagents":[{"agentId":"agent_child"')
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
})
