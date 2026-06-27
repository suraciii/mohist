import { describe, expect, it } from "vitest"
import { sessionTargetFromContext } from "../../src/actions/acp/session-events.js"
import type { ActionContext } from "../../src/core/types.js"

function baseContext(overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    workflowRunId: "wf-1",
    workId: "work-1",
    workType: "task",
    variables: {},
    workDir: "D:/work",
    signal: new AbortController().signal,
    writeVars: async () => {},
    ...overrides,
  }
}

describe("sessionTargetFromContext", () => {
  it("AgentJobWithSessionId_ReturnsGenericShape", () => {
    const context = baseContext({
      ownerKind: "agent-job",
      agentSessionId: "session-abc",
      projectId: "project-1",
      workflowRunId: "",
    })

    const target = sessionTargetFromContext(context)

    expect(target).toEqual({ kind: "generic", projectId: "project-1", sessionId: "session-abc" })
  })

  it("AgentJobWithSessionId_IgnoresWithSessionName", () => {
    const context = baseContext({
      ownerKind: "agent-job",
      agentSessionId: "session-abc",
      projectId: "project-1",
      workflowRunId: "",
      with: { session: "ignored-name" },
    })

    const target = sessionTargetFromContext(context)

    expect(target).toEqual({ kind: "generic", projectId: "project-1", sessionId: "session-abc" })
  })

  it("AgentJobWithoutSessionId_FallsThroughToWorkflowShape", () => {
    const context = baseContext({
      ownerKind: "agent-job",
      projectId: "project-1",
      workflowRunId: "",
      workId: "fallback-work",
    })

    expect(sessionTargetFromContext(context)).toEqual({ kind: "workflow", projectId: "project-1", workflowRunId: "", sessionName: "fallback-work" })
  })

  it("WorkflowOwnerKind_ReturnsWorkflowShape_WithSessionName", () => {
    const context = baseContext({
      ownerKind: "workflow",
      projectId: "project-1",
      workflowRunId: "wf-1",
      with: { session: "build" },
    })

    expect(sessionTargetFromContext(context)).toEqual({ kind: "workflow", projectId: "project-1", workflowRunId: "wf-1", sessionName: "build" })
  })

  it("OwnerKindAbsent_FallsBackToWorkflowShape", () => {
    const context = baseContext({
      projectId: "project-1",
      workflowRunId: "wf-1",
      workId: "fallback-work",
    })

    expect(sessionTargetFromContext(context)).toEqual({ kind: "workflow", projectId: "project-1", workflowRunId: "wf-1", sessionName: "fallback-work" })
  })

  it("ProjectIdMissing_ReturnsNull", () => {
    const context = baseContext({
      ownerKind: "agent-job",
      agentSessionId: "session-abc",
      projectId: undefined,
      workflowRunId: "",
    })

    expect(sessionTargetFromContext(context)).toBeNull()
  })

  it("AgentJobWithSessionNameButNoAgentSessionId_FallsThroughToWorkflowShape", () => {
    const context = baseContext({
      ownerKind: "agent-job",
      projectId: "project-1",
      workflowRunId: "",
      with: { session: "ephemeral" },
    })

    expect(sessionTargetFromContext(context)).toEqual({ kind: "workflow", projectId: "project-1", workflowRunId: "", sessionName: "ephemeral" })
  })
})
