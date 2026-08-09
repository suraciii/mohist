import { describe, expect, it } from "vitest"
import { projectTaskOutput } from "../src/runtime/executor.js"
import { agentObservation } from "../src/actions/workflow-agent-turn.js"
import type { ActionCapabilitySet } from "../src/actions/manifest.js"
import type { DispatchWorkItem } from "../src/core/types.js"

const work: DispatchWorkItem = {
  workflowRunId: "workflow-1",
  workId: "task-1.1",
  workType: "task",
  uses: "mohist/pi",
  with: { prompt: "review" },
  projectId: "project-1",
}

const agentTurnCaps = new Set(["agent-turn"]) as unknown as ActionCapabilitySet

describe("mohist/agent TaskRun output contract", () => {
  it("projects a completed observation with the full cross-locatable identity", () => {
    const observation = agentObservation({
      agentId: "agent-reviewer",
      sessionId: "session-1",
      inputId: "input-1",
      turnId: "turn-1",
    }, "completed", "completed", null, null, "final answer")

    const result = projectTaskOutput(work, { output: null }, null, agentTurnCaps, observation)

    expect(result).toEqual({ status: "completed", output: observation, exitCode: undefined })
  })

  it("keeps a failed or cancelled observation visible without replaying it as an AgentJob", () => {
    const observation = agentObservation({
      agentId: "agent-reviewer",
      sessionId: "session-1",
      inputId: "input-1",
      turnId: "turn-1",
    }, "cancelled", "cancelled", "interrupted", "recover", null)

    const result = projectTaskOutput(work, { error: { code: "interrupted", message: "cancelled" } }, null, agentTurnCaps, observation)

    expect(result).toMatchObject({
      status: "failed",
      error: { code: "interrupted" },
      output: observation,
    })
  })
})
