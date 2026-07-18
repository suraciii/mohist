import { mkdir, stat, utimes, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setCleanupAgentActionForTest, setExecutorLockHolderProbeForTest, setWorktreeClockForTest } from "../src/runtime/worktree-enforcement.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionContext, ActionResult, RenderedWorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import { createTestTempDir } from "./support/temp-dir.js"

const FIXED_NOW_MS = 1_000_000

let workDir: string
let connection: Pick<ServerConnection, "uploadArtifact" | "report">
let worktree: FakeWorktree

beforeEach(async () => {
  workDir = await createTestTempDir("mohist-executor-cleanup-")
  await mkdir(join(workDir, ".git"), { recursive: true })
  worktree = { workDir, branch: "main", staged: [], unstaged: [], untracked: [] }
  installExecutorGit(worktree)
  setWorktreeClockForTest(() => FIXED_NOW_MS)
  connection = {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in cleanup tests")
    },
  } as unknown as Pick<ServerConnection, "uploadArtifact" | "report">
})

afterEach(() => {
  setCleanupAgentActionForTest(null)
  setExecutorGitRunnerForTest(null)
  setExecutorLockHolderProbeForTest(null)
  setWorktreeClockForTest(null)
})

type FakeWorktree = {
  workDir: string
  branch: string
  staged: string[]
  unstaged: string[]
  untracked: string[]
}

function installExecutorGit(state: FakeWorktree) {
  setExecutorGitRunnerForTest(async (observedWorkDir, args) => {
    expect(observedWorkDir).toBe(state.workDir)
    switch (args.join(" ")) {
      case "rev-parse --abbrev-ref HEAD":
        return gitOk(`${state.branch}\n`)
      case "rev-parse --is-inside-work-tree":
        return gitOk("true\n")
      case "diff --cached --name-only":
        return gitOk(fileList(state.staged))
      case "diff --name-only":
        return gitOk(fileList(state.unstaged))
      case "ls-files --others --exclude-standard":
        return gitOk(fileList(state.untracked))
      case "rev-parse --git-path index.lock":
        return gitOk(join(state.workDir, ".git", "index.lock"))
      default:
        throw new Error(`unexpected executor git call: ${args.join(" ")}`)
    }
  })
}

function markWorktreeDirty(path: string, category: "staged" | "unstaged" | "untracked" = "unstaged") {
  worktree[category].push(path)
}

function commitCleanup(paths: string[]) {
  for (const path of paths) {
    worktree.staged = worktree.staged.filter((entry) => entry !== path)
    worktree.unstaged = worktree.unstaged.filter((entry) => entry !== path)
    worktree.untracked = worktree.untracked.filter((entry) => entry !== path)
  }
}

function expectWorktreeClean() {
  expect({ staged: worktree.staged, unstaged: worktree.unstaged, untracked: worktree.untracked })
    .toEqual({ staged: [], unstaged: [], untracked: [] })
}

function fileList(paths: string[]) {
  return paths.length === 0 ? "" : `${paths.join("\n")}\n`
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 128) {
  return { success: false, stdout: "", stderr, exitCode, combinedOutput: stderr }
}

function makeRegistry(handler: (ctx: ActionContext) => Promise<ActionResult>): ActionRegistry {
  const registry = new ActionRegistry()
  registry.register("core/script", async (ctx) => handler(ctx))
  registry.register("mohist/acp-agent", async (ctx) => handler(ctx))
  return registry
}

function buildExecutor(registry: ActionRegistry): WorkExecutor {
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch: "main", changeDir: null }),
    connection as never,
    {} as never,
    null,
    workDir,
  )
}

function buildWork(overrides: Partial<RenderedWorkItem> = {}): RenderedWorkItem {
  return {
    workflowRunId: "wf-1",
    workId: "work-1",
    workType: "task",
    title: "Cleanup test task",
    uses: "core/script",
    with: {},
    variables: {
      workspace: { path: workDir, branch: "main", changeDir: null },
    },
    ...overrides,
  }
}

describe("WorkExecutor clean worktree invariant", () => {
  it("completesTaskImmediatelyWhenWorktreeIsClean", async () => {
    // The action succeeds and the worktree is already clean (the
    // script does not touch the filesystem). The runner must not
    // enter the cleanup loop and must not attach a cleanup count to
    // the result.
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "ok" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.message).toBe("ok")
    expect(result.cleanupAttempts).toBeUndefined()
  })

  it("failsDeterministicTaskImmediatelyWhenWorktreeIsDirty", async () => {
    // Deterministic (non-agent) action leaves a modified file. The
    // runner must fail the task with structured dirty-worktree
    // evidence listing staged, unstaged, and untracked files.
    markWorktreeDirty("src/leftover.ts")
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "ran" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.cleanupAttempts).toBe(0)
    expect(result.message).toMatch(/worktree dirty/)
    expect(result.message).toMatch(/unstaged=\[src\/leftover\.ts\]/)
    expect(result.output).toBeDefined()
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "dirty-worktree",
      staged: [],
      unstaged: ["src/leftover.ts"],
      untracked: [],
      cleanupAttempts: 0,
    })
  })

  it("failsDeterministicTaskWithStagedAndUntrackedCategories", async () => {
    // Three file categories must all appear in the structured
    // evidence: staged (added to index), unstaged (modified), and
    // untracked (not in index).
    markWorktreeDirty("src/staged.ts", "staged")
    markWorktreeDirty("src/unstaged.ts")
    markWorktreeDirty("src/untracked.ts", "untracked")

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence.kind).toBe("dirty-worktree")
    expect(evidence.staged).toEqual(["src/staged.ts"])
    expect(evidence.unstaged).toEqual(["src/unstaged.ts"])
    expect(evidence.untracked).toEqual(["src/untracked.ts"])
    expect(evidence.cleanupAttempts).toBe(0)
  })

  it("succeedsAfterSingleCleanupAttemptWhenAgentCommitsLeftoverChanges", async () => {
    // Agent-backed task leaves a modified file. The cleanup action
    // resolves it, so the next worktree probe is clean.
    markWorktreeDirty("src/forgot.ts")
    setCleanupAgentActionForTest(async (ctx) => {
      const prompt = String(ctx.with?.prompt ?? "")
      // The cleanup prompt must explicitly constrain the agent.
      expect(prompt).toMatch(/do NOT start any new task work/i)
      expect(prompt).toMatch(/do NOT push to any remote/i)
      expect(prompt).toMatch(/commit task-related changes or revert unrelated ones/i)
      expect(prompt).toMatch(/commit SHA|no-change/i)
      expect(prompt).toContain("src/forgot.ts")
      commitCleanup(["src/forgot.ts"])
      return { status: "success", message: "committed abc1234" }
    })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "agent done" })))

    const result = await executor.execute(buildWork({ uses: "mohist/acp-agent" }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.cleanupAttempts).toBe(1)
    expect(result.message).toBe("agent done")
    expectWorktreeClean()
  })

  it("runs an OpenCode cleanup follow-up through the original Action instead of ACP", async () => {
    let openCodeCalls = 0
    let acpCalls = 0
    const registry = new ActionRegistry()
    registry.register("mohist/opencode", async (ctx) => {
      openCodeCalls += 1
      if (openCodeCalls === 1) {
        markWorktreeDirty("src/opencode-output.ts", "untracked")
      } else {
        expect(String(ctx.with?.prompt ?? "")).toContain("Cleanup Follow-up (attempt 1)")
        expect(ctx.workId).toBe("work-opencode-cleanup")
        expect(ctx.with?.session).toBe("plan")
        commitCleanup(["src/opencode-output.ts"])
      }
      return { status: "success", message: "OpenCode turn completed" }
    })
    registry.register("mohist/acp-agent", async () => {
      acpCalls += 1
      return { status: "failure", message: "legacy ACP action must not run" }
    })
    const executor = buildExecutor(registry)

    const result = await executor.execute(buildWork({
      workId: "work-opencode-cleanup",
      uses: "mohist/opencode",
      with: { session: "plan", prompt: "write output" },
    }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.cleanupAttempts).toBe(1)
    expect(openCodeCalls).toBe(2)
    expect(acpCalls).toBe(0)
    expectWorktreeClean()
  })

  it("clearsStaleGitIndexLockBeforeAgentCleanupCommit", async () => {
    // A stale Git index lock is runner control-plane state, not task
    // output. The runner should clear it before asking the agent to
    // perform the bounded cleanup commit so a crashed previous Git
    // command does not make all cleanup retries fail.
    markWorktreeDirty("src/stale-lock.ts")
    const lockPath = join(workDir, ".git", "index.lock")
    await writeFile(lockPath, "", "utf8")
    const old = new Date(FIXED_NOW_MS - 120_000)
    await utimes(lockPath, old, old)

    let cleanupCalls = 0
    setExecutorLockHolderProbeForTest(async () => ({ held: false }))
    setCleanupAgentActionForTest(async () => {
      cleanupCalls += 1
      await expect(stat(lockPath)).rejects.toMatchObject({ code: "ENOENT" })
      commitCleanup(["src/stale-lock.ts"])
      return { status: "success", message: "committed stale lock cleanup" }
    })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "agent done" })))

    const result = await executor.execute(buildWork({ uses: "mohist/acp-agent" }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.cleanupAttempts).toBe(1)
    expect(cleanupCalls).toBe(1)
    await expect(stat(lockPath)).rejects.toMatchObject({ code: "ENOENT" })
    expectWorktreeClean()
  })

  it("doesNotClearStaleGitIndexLockWhenStillHeld", async () => {
    markWorktreeDirty("src/held-lock.ts")
    const lockPath = join(workDir, ".git", "index.lock")
    await writeFile(lockPath, "", "utf8")
    const old = new Date(FIXED_NOW_MS - 120_000)
    await utimes(lockPath, old, old)
    let cleanupCalls = 0
    setExecutorLockHolderProbeForTest(async (_workDir, observedPath) => {
      expect(observedPath).toBe(lockPath)
      return { held: true, detail: "git 1234" }
    })
    setCleanupAgentActionForTest(async () => {
      cleanupCalls += 1
      return { status: "success" }
    })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "agent done" })))

    const result = await executor.execute(buildWork({ uses: "mohist/acp-agent" }), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.cleanupAttempts).toBe(0)
    expect(cleanupCalls).toBe(0)
    expect(result.message).toMatch(/still held/i)
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "git-index-lock",
      reason: "git index lock is still held: git 1234",
      unstaged: ["src/held-lock.ts"],
    })
    await stat(lockPath)
  })

  it("doesNotAskAgentCleanupWhenGitIndexLockIsFresh", async () => {
    // A fresh lock may still belong to a running Git process. The
    // runner must not remove it or spend cleanup attempts by sending
    // the agent into a commit that cannot succeed.
    markWorktreeDirty("src/fresh-lock.ts")
    const lockPath = join(workDir, ".git", "index.lock")
    await writeFile(lockPath, "", "utf8")
    await utimes(lockPath, new Date(FIXED_NOW_MS - 1), new Date(FIXED_NOW_MS - 1))
    let cleanupCalls = 0
    setCleanupAgentActionForTest(async () => {
      cleanupCalls += 1
      return { status: "success" }
    })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "agent done" })))

    const result = await executor.execute(buildWork({ uses: "mohist/acp-agent" }), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.cleanupAttempts).toBe(0)
    expect(cleanupCalls).toBe(0)
    expect(result.message).toMatch(/git index lock blocked cleanup/i)
    expect(result.message).toMatch(/fresh/i)
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "git-index-lock",
      cleanupAttempts: 0,
      unstaged: ["src/fresh-lock.ts"],
    })
    expect(evidence.lockPath).toBe(lockPath)
    await stat(lockPath)
  })

  it("rendersTemplateVariablesInCleanupExpectationPaths", async () => {
    // Cleanup follow-ups inherit the original task's completion
    // requirements. Those requirements are filesystem paths, so they
    // must use the same rendered `with` object as the original action
    // rather than the raw workflow template.
    markWorktreeDirty("openspec/changes/cli-interface-update/specs/cli-interface/spec.md")
    let observedExpectedPath: unknown
    setCleanupAgentActionForTest(async (ctx) => {
      const expectInput = ctx.with?.expect
      if (expectInput && typeof expectInput === "object" && !Array.isArray(expectInput)) {
        const files = expectInput.files
        if (Array.isArray(files) && files[0] && typeof files[0] === "object") {
          observedExpectedPath = (files[0] as { path?: unknown }).path
        }
      }
      commitCleanup(["openspec/changes/cli-interface-update/specs/cli-interface/spec.md"])
      return { status: "success" }
    })

    const work = buildWork({
      uses: "mohist/acp-agent",
      with: {
        prompt: "write specs",
        expect: { files: [{ path: "${{ openspecChangeDir }}/specs" }] },
      },
      variables: {
        workspace: { path: workDir, branch: "main", changeDir: null },
        openspecChangeDir: "openspec/changes/cli-interface-update",
      },
    })
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(observedExpectedPath).toBe("openspec/changes/cli-interface-update/specs")
  })

  it("succeedsAfterMultipleCleanupAttemptsWithTotalCountRecorded", async () => {
    // First cleanup only partially resolves the worktree (one
    // file committed, another left). Second cleanup commits the
    // remaining file. The task completes with cleanupAttempts=2.
    markWorktreeDirty("src/keep.ts")
    markWorktreeDirty("src/extra.ts")
    let attempt = 0
    setCleanupAgentActionForTest(async (ctx) => {
      attempt += 1
      const prompt = String(ctx.with?.prompt ?? "")
      expect(prompt).toContain(`attempt ${attempt}`)
      if (attempt === 1) {
        commitCleanup(["src/keep.ts"])
        return { status: "success", message: "partial" }
      }
      commitCleanup(["src/extra.ts"])
      return { status: "success", message: "full" }
    })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(buildWork({ uses: "mohist/acp-agent" }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.cleanupAttempts).toBe(2)
    expect(attempt).toBe(2)
  })

  it("failsWithStructuredEvidenceWhenCleanupAttemptsExhausted", async () => {
    // The agent keeps returning success but the worktree stays
    // dirty. The runner must stop after the default 3 attempts and
    // fail with structured evidence carrying the categorized file
    // lists and cleanupAttempts=3.
    markWorktreeDirty("src/never.ts")
    const cleanupCalls: number[] = []
    setCleanupAgentActionForTest(async (ctx) => {
      cleanupCalls.push(cleanupCalls.length + 1)
      const prompt = String(ctx.with?.prompt ?? "")
      // The cleanup prompt must remain constrained on every retry.
      expect(prompt).toMatch(/do NOT push to any remote/i)
      return { status: "success", message: "noop" }
    })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "first run" })))

    const result = await executor.execute(buildWork({ uses: "mohist/acp-agent" }), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.cleanupAttempts).toBe(3)
    expect(cleanupCalls).toEqual([1, 2, 3])
    expect(result.message).toMatch(/worktree dirty after 3 cleanup attempt/)
    expect(result.message).toMatch(/unstaged=\[src\/never\.ts\]/)
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "dirty-worktree",
      staged: [],
      unstaged: ["src/never.ts"],
      untracked: [],
      cleanupAttempts: 3,
    })
  })

  it("respectsRunnerCleanupMaxAttemptsOverrideBelowDefault", async () => {
    // Variables can lower the cleanup bound to 1. After the single
    // failed attempt the runner must fail with cleanupAttempts=1.
    markWorktreeDirty("src/stubborn.ts")
    setCleanupAgentActionForTest(async () => ({ status: "success", message: "did nothing" }))

    const work = buildWork({ uses: "mohist/acp-agent" })
    work.variables = {
      ...(work.variables ?? {}),
      runner: { cleanup: { maxAttempts: 1 } },
    }
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.cleanupAttempts).toBe(1)
  })

  it("reusesSameAgentSessionByCarryingOriginalWorkIdIntoCleanupContext", async () => {
    // The cleanup follow-up must run in the same agent session as
    // the original task. The acpAgentAction keys its session cache
    // by `(workflowRunId, sessionName)` and falls back to
    // `workId` for the session name, so the cleanup context must
    // carry the original workId and the same `with.session` value
    // to reuse the same session rather than start a new one.
    markWorktreeDirty("src/session.ts")
    const observedWorkIds: string[] = []
    const observedSessions: (string | undefined)[] = []
    setCleanupAgentActionForTest(async (ctx) => {
      observedWorkIds.push(ctx.workId)
      const session = typeof ctx.with?.["session"] === "string" ? ctx.with["session"] : undefined
      observedSessions.push(session)
      expect(ctx.workflowRunId).toBe("wf-1")
      expect(ctx.stage).toBe("integrate")
      expect(ctx.projectId).toBe("project-1")
      expect(ctx.issueNumber).toBe(255)
      expect(ctx.ownerKind).toBe("workflow")
      expect(ctx.agentSessionId).toBe("agent-session-1")
      expect(ctx.serverConnection).toBe(connection)
      expect(typeof ctx.writeVars).toBe("function")
      const prompt = String(ctx.with?.prompt ?? "")
      // The runner must NOT add a new session; it must preserve
      // either the explicit `with.session` from the original task
      // or fall back to the workId.
      expect(session === "named-session" || session === undefined).toBe(true)
      expect(ctx.workId).toBe("work-session-test")
      expect(prompt).toContain("attempt 1")
      commitCleanup(["src/session.ts"])
      return { status: "success" }
    })

    const work: RenderedWorkItem = {
      workflowRunId: "wf-1",
      workId: "work-session-test",
      workType: "task",
      stage: "integrate",
      title: "Session reuse test",
      uses: "mohist/acp-agent",
      with: { session: "named-session", prompt: "do the work" },
      variables: { workspace: { path: workDir, branch: "main", changeDir: null } },
      projectId: "project-1",
      issueNumber: 255,
      ownerKind: "workflow",
      agentSessionId: "agent-session-1",
    }
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.cleanupAttempts).toBe(1)
    expect(observedWorkIds).toEqual(["work-session-test"])
    expect(observedSessions).toEqual(["named-session"])
  })

  it("reusesSameAgentSessionWhenNoExplicitSessionNameIsProvided", async () => {
    // When the original work has no `with.session`, the acpAgentAction
    // derives the session name from `workId`. The cleanup must
    // therefore reuse the same workId so the same session is hit.
    markWorktreeDirty("src/default-session.ts")
    const observedWorkIds: string[] = []
    setCleanupAgentActionForTest(async (ctx) => {
      observedWorkIds.push(ctx.workId)
      expect(ctx.with?.["session"]).toBeUndefined()
      commitCleanup(["src/default-session.ts"])
      return { status: "success" }
    })

    const work: RenderedWorkItem = {
      workflowRunId: "wf-1",
      workId: "work-default-session",
      workType: "task",
      title: "Default session test",
      uses: "mohist/acp-agent",
      with: { prompt: "do the work" },
      variables: { workspace: { path: workDir, branch: "main", changeDir: null } },
    }
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(observedWorkIds).toEqual(["work-default-session"])
  })

  it("cleanupFailureCountsAsAttemptAndStopsLoopEarly", async () => {
    // The cleanup action itself returns a failure status. The
    // runner counts that as a real attempt and stops; the failure
    // evidence lists the categorized files at that point.
    markWorktreeDirty("src/abort.ts")
    setCleanupAgentActionForTest(async () => ({ status: "failure", message: "agent crashed" }))

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(buildWork({ uses: "mohist/acp-agent" }), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.cleanupAttempts).toBe(1)
    expect(result.message).toMatch(/Cleanup attempt 1 failed: agent crashed/)
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence.unstaged).toEqual(["src/abort.ts"])
    expect(evidence.cleanupAttempts).toBe(1)
  })

  it("preservesOriginalActionOutputAlongsideDirtyWorktreeEvidence", async () => {
    // When the action produced structured output (e.g. an acp-agent
    // result blob), the dirty-worktree failure must merge that
    // output with the evidence rather than dropping it.
    markWorktreeDirty("src/preserved.ts")
    const actionOutput = JSON.stringify({ kind: "acp-agent", runtimeSessionId: "sess-1", model: "openai/gpt-5" })
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", output: actionOutput })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence.kind).toBe("dirty-worktree")
    expect(evidence.runtimeSessionId).toBe("sess-1")
    expect(evidence.model).toBe("openai/gpt-5")
    expect(evidence.unstaged).toEqual(["src/preserved.ts"])
  })

  it("treatsNonGitWorktreeAsClean", async () => {
    // Defensive behaviour: the executor should not crash when the
    // worktree is not a git repository (e.g. test fixtures that
    // resolve to a plain tmpdir). It must treat the worktree as
    // clean so the task can still complete.
    const plainDir = await createTestTempDir("mohist-executor-cleanup-plain-")
    setExecutorGitRunnerForTest(async (observedWorkDir) => {
      expect(observedWorkDir).toBe(plainDir)
      return gitFail("fatal: not a git repository (or any of the parent directories): .git")
    })
    const executor = new WorkExecutor(
      makeRegistry(async () => ({ status: "success", message: "ran" })),
      verifyOnlyWorkspaceManager({ path: plainDir, branch: null, changeDir: null }),
      connection as never,
      {} as never,
      null,
      plainDir,
    )

    const work: RenderedWorkItem = {
      workflowRunId: "wf-1",
      workId: "work-plain",
      workType: "task",
      title: "Plain worktree test",
      uses: "core/script",
      with: {},
      variables: { workspace: { path: plainDir, branch: null, changeDir: null } },
    }

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.cleanupAttempts).toBeUndefined()
  })

  it("cleanupPromptCarriesExplicitConstraintsAndOriginalPromptContext", async () => {
    // The cleanup prompt must name the file categories, instruct
    // the agent to report a commit SHA or no-change, and include
    // a short reference to the original task prompt for context.
    markWorktreeDirty("src/context.ts")
    let capturedPrompt: string | undefined
    setCleanupAgentActionForTest(async (ctx) => {
      capturedPrompt = String(ctx.with?.prompt ?? "")
      commitCleanup(["src/context.ts"])
      return { status: "success" }
    })

    const work = buildWork({ uses: "mohist/acp-agent", with: { prompt: "Refactor the parser" } })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(capturedPrompt).toBeDefined()
    expect(capturedPrompt).toContain("Cleanup Follow-up (attempt 1)")
    expect(capturedPrompt).toContain("Refactor the parser")
    expect(capturedPrompt).toContain("Staged (added to index)")
    expect(capturedPrompt).toContain("Unstaged (modified in working tree)")
    expect(capturedPrompt).toContain("src/context.ts")
    expect(capturedPrompt).toMatch(/commit SHA\(s\)|no-change/)
    expect(capturedPrompt).toMatch(/do NOT push to any remote/i)
    expect(capturedPrompt).toMatch(/do NOT start any new task work/i)
  })

  it("worktreeProbeFailure_FailsTaskWithStructuredEvidence", async () => {
    // When the worktree probe itself fails (corrupted worktree, missing
    // git binary, permission error), the executor must fail the task
    // with structured evidence rather than silently treat the worktree
    // as clean. Only the "not a git repository" stderr is treated as
    // the legitimate plain-tmpdir case. The branch-stability check runs
    // first, so a corrupted worktree now surfaces as a
    // branch-invariant-violation with a probe-failure detail rather
    // than a dirty-worktree failure.
    setExecutorGitRunnerForTest(async (_workDir, args) => {
      if (args[0] === "rev-parse" && args[1] === "--abbrev-ref" && args[2] === "HEAD") {
        return {
          success: false,
          stdout: "",
          stderr: "fatal: unable to access '.git': Permission denied",
          exitCode: 128,
          combinedOutput: "fatal: unable to access '.git': Permission denied",
        }
      }
      throw new Error(`unexpected git call: ${args.join(" ")}`)
    })
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "ran" })))
    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.cleanupAttempts).toBeUndefined()
    expect(result.message).toMatch(/branch-invariant violation at start boundary/)
    expect(result.message).toMatch(/probe failed/)
    expect(result.output).toBeDefined()
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "branch-invariant-violation",
      boundary: "start",
      expectedBranch: "main",
      observedBranch: "",
    })
    expect(evidence.detail).toMatch(/probe failed/)
  })

  it("cleanupReplacesLoaderSpecPromptWithLiteralCleanupString", async () => {
    // When the original task carries a structured (loader) prompt
    // instead of a literal string, the cleanup loop replaces the
    // prompt with a literal cleanup string. The structured prompt is
    // not preserved into the cleanup follow-up because the executor
    // does not know how to load it; the constraint must therefore be
    // documented so callers don't depend on the loader resolution
    // during cleanup.
    markWorktreeDirty("src/loader.ts")
    let capturedPrompt: unknown
    setCleanupAgentActionForTest(async (ctx) => {
      capturedPrompt = ctx.with?.prompt
      commitCleanup(["src/loader.ts"])
      return { status: "success" }
    })

    const work: RenderedWorkItem = {
      workflowRunId: "wf-1",
      workId: "work-loader",
      workType: "task",
      title: "Loader prompt test",
      uses: "mohist/acp-agent",
      with: {
        prompt: {
          uses: "mohist/openspec-task-prompt",
          with: { file: "tasks.json", items: "tasks", taskId: "T-1" },
        },
      },
      variables: { workspace: { path: workDir, branch: "main", changeDir: null } },
    }
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(typeof capturedPrompt).toBe("string")
    // The cleanup prompt is a literal cleanup string; the original
    // loader spec is replaced (the executor does not resolve loaders
    // during cleanup) so the agent receives the standard cleanup
    // instructions instead of the structured prompt.
    expect(capturedPrompt as string).toContain("Cleanup Follow-up")
    expect(capturedPrompt as string).toContain("### Hard constraints")
    // Make sure the loader-spec structure was not preserved: the
    // captured prompt is a plain string, not an object with `uses` /
    // `with` keys.
    expect(typeof capturedPrompt).not.toBe("object")
  })
})
