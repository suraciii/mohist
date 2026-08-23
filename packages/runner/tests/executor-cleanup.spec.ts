import { describe, expect, it } from "vitest"
import {
  dirtyWorktreeFailure,
  runAgentCleanupAttempt,
  worktreeProbeFailure,
  WorktreeProbeError,
} from "../src/runtime/worktree-enforcement.js"

describe("worktree failure protocol", () => {
  it("reports a dirty worktree as an error instead of augmenting output", () => {
    const result = dirtyWorktreeFailure(
      { status: "completed", output: { promise: "PASS" }, artifactUploadIds: ["artup_1"] },
      { staged: [], unstaged: ["src/leftover.ts"], untracked: [], isClean: false },
      0,
    )
    expect(result).toMatchObject({ status: "failed", error: { code: "worktree-dirty" } })
    expect(result.output).toEqual({ promise: "PASS" })
    expect(result.artifactUploadIds).toEqual(["artup_1"])
  })

  it("preserves cleanup delivery wait timeout evidence instead of wrapping it as dirty-worktree", async () => {
    const result = await runAgentCleanupAttempt(
      {
        workflowRunId: "wf",
        workId: "task",
        workType: "task",
        stage: "build",
        title: "Build",
        uses: "mohist/opencode",
        with: { prompt: "work" },
      },
      "/workspace",
      null,
      {},
      { staged: [], unstaged: ["src/leftover.ts"], untracked: [], isClean: false },
      2,
      new AbortController().signal,
      async () => ({
        error: {
          code: "session-delivery-wait-timeout",
          message: "waited for project/workflow/session for work item task; exhausted budget 123ms",
        },
      }),
      { buildHost: () => ({}) as never },
      { status: "completed", message: "action completed", cleanupAttempts: 0 },
    )

    expect(result).toMatchObject({
      status: "failed",
      error: { code: "session-delivery-wait-timeout" },
      cleanupAttempts: 2,
    })
    expect(result).not.toMatchObject({ error: { code: "worktree-dirty" } })
    expect(result).not.toBe("ok")
    if (result === "ok") throw new Error("expected cleanup failure")
    expect(result.message).toContain("123ms")
  })

  it("reports a worktree probe failure as a structured error", () => {
    const result = worktreeProbeFailure({
      workflowRunId: "wf", workId: "task", workType: "task", stage: "build", title: "Build", uses: "core/script", with: {},
    }, new WorktreeProbeError("git status failed", 128), { status: "completed", artifactUploadIds: ["artup_2"] })
    expect(result).toMatchObject({ status: "failed", error: { code: "worktree-probe-failed" } })
    expect(result.output).toBeUndefined()
    expect(result.artifactUploadIds).toEqual(["artup_2"])
  })
})
