import { describe, expect, it } from "vitest"
import { dirtyWorktreeFailure, worktreeProbeFailure, WorktreeProbeError } from "../src/runtime/worktree-enforcement.js"

describe("worktree failure protocol", () => {
  it("reports a dirty worktree as an error instead of augmenting output", () => {
    const result = dirtyWorktreeFailure(
      { status: "completed", output: { promise: "PASS" } },
      { staged: [], unstaged: ["src/leftover.ts"], untracked: [], isClean: false },
      0,
    )
    expect(result).toMatchObject({ status: "failed", error: { code: "worktree-dirty" } })
    expect(result.output).toEqual({ promise: "PASS" })
  })

  it("reports a worktree probe failure as a structured error", () => {
    const result = worktreeProbeFailure({
      workflowRunId: "wf", workId: "task", workType: "task", stage: "build", title: "Build", uses: "core/script", with: {},
    }, new WorktreeProbeError("git status failed", 128))
    expect(result).toMatchObject({ status: "failed", error: { code: "worktree-probe-failed" } })
    expect(result.output).toBeUndefined()
  })
})
