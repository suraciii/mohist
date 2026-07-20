import { describe, expect, it } from "vitest"
import type { RenderedWorkItem } from "../src/core/types.js"
import { tryRecovery } from "../src/runtime/recovery.js"

function work(recovery: RenderedWorkItem["recovery"]): RenderedWorkItem {
  return {
    workflowRunId: "wf-recovery",
    workId: "integrate:rebase.2",
    workType: "task",
    stage: "integrate",
    title: "Rebase branch",
    uses: "mohist/rebase",
    with: { baseBranch: "master" },
    recovery,
    recoveryRemaining: 1,
  }
}

describe("recovery action error protocol", () => {
  it("matches an Action error by error.code and preserves the error", () => {
    const result = tryRecovery(work({
      budget: 1,
      handlers: [{
        when: "error.code=conflict",
        tasks: [{ id: "resolve", title: "Resolve", with: { message: "${{ failure.error.message }}" } }],
        retrySelf: true,
      }],
    }), {
      status: "failed",
      error: { code: "conflict", message: "Rebase stopped on a conflict." },
    })

    expect(result).toMatchObject({
      status: "completed",
      error: { code: "conflict", message: "Rebase stopped on a conflict." },
      addTasks: [
        { id: "resolve", with: { message: "Rebase stopped on a conflict." } },
        { id: "integrate:rebase", recoveryRemaining: 0 },
      ],
    })
  })

  it("uses a default handler only for failures", () => {
    const recovery = { budget: 1, handlers: [{ tasks: [{ id: "fix", title: "Fix" }], retrySelf: false }] }
    expect(tryRecovery(work(recovery), { status: "failed", error: { code: "timeout", message: "Timed out" } }))
      .toMatchObject({ status: "completed", addTasks: [{ id: "fix" }] })
    expect(tryRecovery(work(recovery), { status: "completed", output: JSON.stringify({ promise: "PASS" }) })).toBeNull()
  })

  it("matches successful completion output with output.promise", () => {
    const result = tryRecovery(work({
      budget: 1,
      handlers: [{ when: "output.promise=FAIL", tasks: [{ id: "fix", title: "Fix" }], retrySelf: false }],
    }), { status: "completed", output: JSON.stringify({ promise: "FAIL" }) })
    expect(result).toMatchObject({ status: "completed", addTasks: [{ id: "fix" }] })
  })
})
