import { mkdtemp, rm } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionResult, JsonObject, RenderedWorkItem, WorkItemResult } from "../src/core/types.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { tryRecovery } from "../src/runtime/recovery.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

let workDir: string

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-recovery-"))
  setExecutorGitRunnerForTest(async () => ({ success: false, exitCode: 128, stdout: "", stderr: "not a git repository", combinedOutput: "not a git repository" }))
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

function executorFor(result: ActionResult): WorkExecutor {
  const registry = new ActionRegistry()
  registry.register("test/action", async () => result)
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
    {} as never,
    {} as never,
    null,
    workDir,
  )
}

function work(overrides: Partial<RenderedWorkItem> = {}): RenderedWorkItem {
  return {
    workflowRunId: "wf-recovery",
    workId: "integrate:rebase.2",
    workType: "task",
    stage: "integrate",
    title: "Rebase branch",
    uses: "test/action",
    with: { baseBranch: "master" },
    variables: { workspace: { path: workDir, branch: null, changeDir: null } },
    ...overrides,
  }
}

describe("WorkExecutor recovery", () => {
  it("schedules handler tasks and trimmed retry self with decremented remaining state", async () => {
    const executor = executorFor({
      status: "failure",
      message: "conflict",
      output: JSON.stringify({ errorCode: "rebase-conflict" }),
    })

    const result = await executor.execute(work({
      recovery: {
        budget: 2,
        handlers: [
          {
            when: "errorCode=rebase-conflict",
            tasks: [{ id: "resolve-conflicts", title: "Resolve conflicts", uses: "mohist/acp-agent", with: { session: "integrate" } }],
            retrySelf: true,
          },
        ],
      },
      recoveryRemaining: null,
    }), new AbortController().signal)

    expect(result).toMatchObject({
      status: "completed",
      message: "Rebase branch failed (errorCode=rebase-conflict); recovery scheduled",
      addTasks: [
        { id: "resolve-conflicts", title: "Resolve conflicts", uses: "mohist/acp-agent", with: { session: "integrate" } },
        { id: "integrate:rebase", title: "Rebase branch", uses: "test/action", with: { baseBranch: "master" }, recovery: { budget: 2 }, recoveryRemaining: 1 },
      ],
    })
  })

  it("leaves unmatched failure output failed", async () => {
    const executor = executorFor({
      status: "failure",
      message: "network failed",
      output: JSON.stringify({ errorCode: "network" }),
    })

    const result = await executor.execute(work({
      recovery: {
        budget: 1,
        handlers: [{ when: "errorCode=rebase-conflict", tasks: [{ id: "resolve-conflicts", title: "Resolve conflicts" }], retrySelf: true }],
      },
      recoveryRemaining: null,
    }), new AbortController().signal)

    expect(result).toMatchObject({ status: "failed", message: "network failed" })
    expect(result.addTasks).toBeUndefined()
  })

  it("uses a default handler only for failed results after explicit handlers miss", () => {
    const recovery: JsonObject = {
      budget: 1,
      handlers: [
        { when: "errorCode=conflict", tasks: [{ id: "specific", title: "Specific" }], retrySelf: false },
        { tasks: [{ id: "fix-ci", title: "Fix CI" }], retrySelf: true },
      ],
    }

    const failed = tryRecovery(work({ recovery, recoveryRemaining: 1 }), { status: "failed", message: "worktree dirty" })
    expect(failed).toMatchObject({
      status: "completed",
      message: "Rebase branch failed (default); recovery scheduled",
      addTasks: [{ id: "fix-ci" }, { id: "integrate:rebase", recoveryRemaining: 0 }],
    })

    const completed = tryRecovery(work({ recovery, recoveryRemaining: 1 }), { status: "completed", output: JSON.stringify({ errorCode: "other" }) })
    expect(completed).toBeNull()
  })

  it("expands ${{ failure.output.<field> }} in the recovery handler's `with` using the triggering action output", async () => {
    const executor = executorFor({
      status: "failure",
      message: "checks failed",
      output: JSON.stringify({
        errorCode: "pr-checks-failed",
        prNumber: 42,
        prUrl: "https://example/pr/42",
      }),
    })

    const result = await executor.execute(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=pr-checks-failed",
          tasks: [{
            id: "fix-pr-checks",
            title: "Fix PR checks",
            uses: "mohist/opencode",
            with: { targetPr: "${{ failure.output.prNumber }}" },
          }],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
    }), new AbortController().signal)

    expect(result).toMatchObject({
      status: "completed",
      addTasks: [
        { id: "fix-pr-checks", with: { targetPr: 42 } },
      ],
    })
    const serialized = JSON.stringify(result.addTasks?.[0]?.with)
    expect(serialized).not.toContain("${{ failure.")
  })

  it("pre-renders ${{ prompts.<key> }} bodies inline with ${{ failure.* }} expanded against the triggering output", async () => {
    const executor = executorFor({
      status: "failure",
      message: "checks failed",
      output: JSON.stringify({
        errorCode: "pr-checks-failed",
        prNumber: 42,
        prUrl: "https://example/pr/42",
      }),
    })

    const variables = {
      prompts: {
        "fix-pr-checks":
          "## Context\n\n" +
          "failed for PR #${{ failure.output.prNumber }} (${{ failure.output.prUrl }}). " +
          "See ${{ vars.agent }} branch ${{ workspace.branch }}.",
      },
      vars: { agent: "default" },
      workspace: { branch: "feature/recovery" },
    }

    const result = await executor.execute(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=pr-checks-failed",
          tasks: [{
            id: "fix-pr-checks",
            title: "Fix PR checks",
            uses: "mohist/opencode",
            with: { prompt: "${{ prompts.fix-pr-checks }}" },
          }],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
      variables,
    }), new AbortController().signal)

    const withPrompt = result.addTasks?.[0]?.with?.prompt
    expect(typeof withPrompt).toBe("string")
    expect(withPrompt).toContain("failed for PR #42 (https://example/pr/42).")
    expect(withPrompt).toContain("${{ vars.agent }}")
    expect(withPrompt).toContain("${{ workspace.branch }}")
    expect(withPrompt).not.toContain("${{ prompts.")
    expect(withPrompt).not.toContain("${{ failure.")
  })

  it("fails dispatch with an actionable diagnostic when a recovery handler references an unresolvable ${{ failure.* }} path", async () => {
    const executor = executorFor({
      status: "failure",
      message: "checks failed",
      output: JSON.stringify({
        errorCode: "pr-checks-failed",
        prNumber: 42,
      }),
    })

    const result = await executor.execute(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=pr-checks-failed",
          tasks: [{
            id: "fix-pr-checks",
            title: "Fix PR checks",
            uses: "mohist/opencode",
            with: { targetPr: "${{ failure.output.prNumber }}", comment: "PR #${{ failure.output.prUrl }}" },
          }],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
    }), new AbortController().signal)

    expect(result).toMatchObject({ status: "failed" })
    expect(result.message).toContain("fix-pr-checks")
    expect(result.message).toContain("failure.output.prUrl")
    expect(result.addTasks).toBeUndefined()
  })

  it("treats the triggering action's missing structured output as fully unresolvable for ${{ failure.* }} refs", async () => {
    const executor = executorFor({
      status: "failure",
      message: "checks failed",
      output: JSON.stringify({ errorCode: "pr-checks-failed" }),
    })

    const result = await executor.execute(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=pr-checks-failed",
          tasks: [{
            id: "fix-pr-checks",
            title: "Fix PR checks",
            uses: "mohist/opencode",
            with: { targetPr: "${{ failure.output.prNumber }}" },
          }],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
    }), new AbortController().signal)

    expect(result).toMatchObject({ status: "failed" })
    expect(result.message).toContain("fix-pr-checks")
    expect(result.message).toContain("failure.output.prNumber")
    expect(result.addTasks).toBeUndefined()
  })

  it("constructs recovery tasks that reference no ${{ failure.* }} even when the triggering output lacks expected fields", async () => {
    const executor = executorFor({
      status: "failure",
      message: "checks failed",
      output: JSON.stringify({ errorCode: "pr-checks-failed" }),
    })

    const result = await executor.execute(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=pr-checks-failed",
          tasks: [{
            id: "fix-pr-checks",
            title: "Fix PR checks",
            uses: "mohist/opencode",
            with: { session: "${{ vars.session }}", branch: "${{ workspace.branch }}" },
          }],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
    }), new AbortController().signal)

    expect(result).toMatchObject({
      status: "completed",
      addTasks: [{ id: "fix-pr-checks", with: { session: "${{ vars.session }}", branch: "${{ workspace.branch }}" } }],
    })
    const serialized = JSON.stringify(result.addTasks?.[0]?.with)
    expect(serialized).not.toContain("${{ failure.")
  })

  it("does not expand ${{ failure.* }} in non-recovery task rendering (approval-feedback, retry-self, ordinary task)", async () => {
    const seen: { with: JsonObject | null | undefined } = { with: null }
    const registry = new ActionRegistry()
    registry.register("test/action", async (ctx) => {
      seen.with = ctx.with
      return { status: "completed", output: null }
    })
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      {} as never,
      null,
      workDir,
    )
    const workItem = work({
      uses: "test/action",
      with: {
        agent: "${{ vars.agent }}",
      },
      variables: { vars: { agent: "default" } },
    })
    const result = await executor.execute(workItem, new AbortController().signal)
    expect(result.addTasks).toBeUndefined()
    expect(seen.with).toEqual({ agent: "default" })
  })
})

function recoveryWork(recoveryRemaining: number | null | undefined): RenderedWorkItem {
  return work({
    recovery: {
      budget: 2,
      handlers: [
        {
          when: "errorCode=conflict",
          tasks: [{ id: "fix", title: "Fix", uses: "test/fix" }],
          retrySelf: true,
        },
      ],
    },
    ...(recoveryRemaining === undefined ? {} : { recoveryRemaining }),
  })
}

function matchingResult(status = "failed"): WorkItemResult {
  return { status, output: JSON.stringify({ errorCode: "conflict" }) }
}

describe("tryRecovery", () => {
  it("initializes explicit null from the declaration and preserves immutable configuration", () => {
    const workItem = recoveryWork(null)
    const recovery = workItem.recovery

    const result = tryRecovery(workItem, matchingResult())

    expect(result?.addTasks).toMatchObject([
      { id: "fix", title: "Fix", uses: "test/fix" },
      {
        id: "integrate:rebase",
        title: "Rebase branch",
        uses: "test/action",
        with: { baseBranch: "master" },
        artifacts: undefined,
        setVars: null,
        recovery,
        recoveryRemaining: 1,
      },
    ])
    expect(workItem.recovery).toBe(recovery)
    expect(workItem.recovery).toEqual({
      budget: 2,
      handlers: [
        { when: "errorCode=conflict", tasks: [{ id: "fix", title: "Fix", uses: "test/fix" }], retrySelf: true },
      ],
    })
  })

  it("consumes one allowance and stops at zero", () => {
    const workItem = recoveryWork(1)
    const first = tryRecovery(workItem, matchingResult())
    expect(first?.addTasks?.at(-1)?.recoveryRemaining).toBe(0)

    expect(tryRecovery(recoveryWork(0), matchingResult())).toBeNull()
  })

  it("fails closed when continuation state is absent", () => {
    expect(tryRecovery(recoveryWork(undefined), matchingResult())).toBeNull()
  })

  it("clamps malformed remaining state without expanding a round", () => {
    expect(tryRecovery(recoveryWork(-1), matchingResult())).toBeNull()
    expect(tryRecovery(recoveryWork(99), matchingResult())?.addTasks?.at(-1)?.recoveryRemaining).toBe(1)
  })

  it("does not consume allowance for unmatched output", () => {
    const result = tryRecovery(recoveryWork(2), { status: "failed", output: JSON.stringify({ errorCode: "other" }) })
    expect(result).toBeNull()
  })

  it("selects the first handler for completed matching output", () => {
    const result = tryRecovery(work({
      recovery: {
        budget: 1,
        handlers: [
          { when: "promise=FAIL", tasks: [{ id: "first", title: "First" }], retrySelf: false },
          { when: "promise=FAIL", tasks: [{ id: "second", title: "Second" }], retrySelf: false },
        ],
      },
      recoveryRemaining: 1,
    }), { status: "completed", output: JSON.stringify({ promise: "FAIL" }) })

    expect(result?.addTasks?.map((task) => task.id)).toEqual(["first"])
  })

  it("initializes nested recovery-enabled handler tasks with their own budget", () => {
    const result = tryRecovery(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=conflict",
          tasks: [{
            id: "nested",
            title: "Nested",
            recovery: { budget: 4, handlers: [] },
          }],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
    }), matchingResult())

    expect(result?.addTasks).toMatchObject([{ id: "nested", title: "Nested", recovery: { budget: 4, handlers: [] }, recoveryRemaining: 4 }])
  })

  it("does not append self retry when retrySelf is false and keeps follow-up order", () => {
    const result = tryRecovery(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=conflict",
          tasks: [
            { id: "first", title: "First" },
            { id: "second", title: "Second" },
          ],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
    }), matchingResult())

    expect(result?.addTasks?.map((task) => task.id)).toEqual(["first", "second"])
  })

  it("expands ${{ failure.output.<field> }} in handler task `with` against the triggering action output", () => {
    const result = tryRecovery(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=pr-checks-failed",
          tasks: [{
            id: "fix-pr-checks",
            title: "Fix PR checks",
            uses: "mohist/opencode",
            with: { targetPr: "${{ failure.output.prNumber }}" },
          }],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
    }), { status: "failed", output: JSON.stringify({ errorCode: "pr-checks-failed", prNumber: 42 }) })

    expect(result?.status).toBe("completed")
    expect(result?.addTasks?.[0]?.with).toEqual({ targetPr: 42 })
  })

  it("pre-renders ${{ prompts.<key> }} bodies and expands ${{ failure.* }} inside", () => {
    const result = tryRecovery(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=pr-checks-failed",
          tasks: [{
            id: "fix-pr-checks",
            title: "Fix PR checks",
            uses: "mohist/opencode",
            with: { prompt: "${{ prompts.fix-pr-checks }}" },
          }],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
      variables: {
        prompts: {
          "fix-pr-checks": "failed for PR #${{ failure.output.prNumber }} (${{ failure.output.prUrl }}); agent=${{ vars.agent }}",
        },
      },
    }), { status: "failed", output: JSON.stringify({ errorCode: "pr-checks-failed", prNumber: 42, prUrl: "https://example/pr/42" }) })

    const withPrompt = result?.addTasks?.[0]?.with?.prompt
    expect(typeof withPrompt).toBe("string")
    expect(withPrompt).toContain("failed for PR #42 (https://example/pr/42)")
    expect(withPrompt).toContain("${{ vars.agent }}")
    expect(withPrompt).not.toContain("${{ prompts.")
    expect(withPrompt).not.toContain("${{ failure.")
  })

  it("fails with a diagnostic naming the path and the recovery task when ${{ failure.* }} is unresolvable", () => {
    const result = tryRecovery(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=pr-checks-failed",
          tasks: [{
            id: "fix-pr-checks",
            title: "Fix PR checks",
            uses: "mohist/opencode",
            with: { comment: "PR #${{ failure.output.prUrl }}" },
          }],
          retrySelf: false,
        }],
      },
      recoveryRemaining: 1,
    }), { status: "failed", output: JSON.stringify({ errorCode: "pr-checks-failed", prNumber: 42 }) })

    expect(result?.status).toBe("failed")
    expect(result?.message).toContain("fix-pr-checks")
    expect(result?.message).toContain("failure.output.prUrl")
    expect(result?.addTasks).toBeUndefined()
  })

  it("excludes the retrySelf clone from the failure-context pass", () => {
    const result = tryRecovery(work({
      recovery: {
        budget: 1,
        handlers: [{
          when: "errorCode=pr-checks-failed",
          tasks: [],
          retrySelf: true,
        }],
      },
      recoveryRemaining: 1,
    }), { status: "failed", output: JSON.stringify({ errorCode: "pr-checks-failed", prNumber: 42 }) })

    const retryClone = result?.addTasks?.find((task) => task.id === "integrate:rebase")
    expect(retryClone).toBeDefined()
    expect(retryClone?.with).toEqual({ baseBranch: "master" })
  })
})
