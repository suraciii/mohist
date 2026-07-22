import { describe, expect, it } from "vitest"
import type { DispatchWorkItem } from "../src/core/types.js"
import { tryRecovery } from "../src/runtime/recovery.js"

function work(recovery: DispatchWorkItem["recovery"]): DispatchWorkItem {
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
    expect(tryRecovery(work(recovery), { status: "completed", output: { promise: "PASS" } })).toBeNull()
  })

  it("matches successful completion output with output.promise", () => {
    const result = tryRecovery(work({
      budget: 1,
      handlers: [{ when: "output.promise=FAIL", tasks: [{ id: "fix", title: "Fix" }], retrySelf: false }],
    }), { status: "completed", output: { promise: "FAIL" } })
    expect(result).toMatchObject({ status: "completed", addTasks: [{ id: "fix" }] })
  })

  it("resolves the fix-pr-checks Prompt while preserving vars and expanding failure.error.message", () => {
    const result = tryRecovery(work({
      budget: 1,
      handlers: [{
        when: "error.code=pr-checks-failed",
        tasks: [{
          id: "recover:fix-pr-checks",
          uses: "mohist/opencode",
          with: { prompt: "${{ prompts.fix-pr-checks }}" },
        }],
        retrySelf: false,
      }],
    }), {
      status: "failed",
      error: { code: "pr-checks-failed", message: "PR checks failed" },
    }, {
      prompts: {
        "fix-pr-checks": "Repair PR #${{ vars.github.pr.number }} (${{ vars.github.pr.url }}): ${{ failure.error.message }}",
      },
      github: { pr: { number: 42, url: "https://github.example/pr/42" } },
    })

    expect(result).toMatchObject({
      status: "completed",
      addTasks: [{
        id: "recover:fix-pr-checks",
        with: {
          prompt: "Repair PR #${{ vars.github.pr.number }} (${{ vars.github.pr.url }}): PR checks failed",
        },
      }],
    })
  })

  it("reports the Prompt, expression, and available failure context when recovery rendering fails", () => {
    const result = tryRecovery(work({
      budget: 1,
      handlers: [{
        when: "error.code=pr-checks-failed",
        tasks: [{
          id: "recover:fix-pr-checks",
          with: { prompt: "${{ prompts.fix-pr-checks }}" },
        }],
        retrySelf: false,
      }],
    }), {
      status: "failed",
      error: { code: "pr-checks-failed", message: "PR checks failed" },
    }, {
      prompts: { "fix-pr-checks": "PR #${{ failure.output.prNumber }}" },
    })

    expect(result).toMatchObject({ status: "failed", error: { code: "recovery-reference-unresolved" } })
    expect(result?.message).toContain("Prompt 'fix-pr-checks'")
    expect(result?.message).toContain("${{ failure.output.prNumber }}")
    expect(result?.message).toContain("failure.output is unavailable")
    expect(result?.message).toContain("failure.error fields [code, message]")
  })
})
