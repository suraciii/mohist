import { describe, expect, it } from "vitest"
import type { DispatchWorkItem } from "../src/core/types.js"
import { branchInvariantViolationFailure } from "../src/runtime/branch-stability.js"

describe("branch stability failure", () => {
  it("reports the invariant through error without placing evidence in output", () => {
    const work: DispatchWorkItem = {
      workflowRunId: "wf", workId: "task", workType: "task", stage: "build", title: "Build", uses: "core/script", with: {},
    }
    const result = branchInvariantViolationFailure(work, {
      kind: "branch-invariant-violation", boundary: "start", expectedBranch: "mohist/run-wf", observedBranch: "main",
    })
    expect(result).toMatchObject({
      status: "failed",
      error: { code: "branch-invariant-violation" },
    })
    expect(result.output).toBeUndefined()
  })
})
