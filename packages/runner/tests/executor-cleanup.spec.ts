import { execFile } from "node:child_process"
import { mkdir, mkdtemp, rm, stat, utimes, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join, dirname } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { promisify } from "node:util"
import { WorkExecutor, setCleanupAgentActionForTest, setExecutorGitRunnerForTest, setExecutorLockHolderProbeForTest } from "../src/runtime/executor.js"
import { ActionRegistry } from "../src/actions/registry.js"
import type { ActionContext, ActionResult, WorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"

const exec = promisify(execFile)

let workDir: string
let connection: Pick<ServerConnection, "uploadArtifact" | "report">

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-cleanup-"))
  await initGitRepo(workDir)
  connection = {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in cleanup tests")
    },
  } as unknown as Pick<ServerConnection, "uploadArtifact" | "report">
})

afterEach(async () => {
  setCleanupAgentActionForTest(null)
  setExecutorGitRunnerForTest(null)
  setExecutorLockHolderProbeForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

async function initGitRepo(dir: string) {
  await exec("git", ["init", "--initial-branch=main", "-q"], { cwd: dir })
  await exec("git", ["config", "user.email", "test@example.com"], { cwd: dir })
  await exec("git", ["config", "user.name", "Test"], { cwd: dir })
  await exec("git", ["config", "commit.gpgsign", "false"], { cwd: dir })
  await writeFile(join(dir, "README.md"), "init\n", "utf8")
  await exec("git", ["add", "README.md"], { cwd: dir })
  await exec("git", ["commit", "-m", "init", "-q"], { cwd: dir })
}

async function dirtyRepoWith(file: { path: string; content: string; stage?: boolean; untracked?: boolean; tracked?: boolean }) {
  const full = join(workDir, file.path)
  await mkdir(dirname(full), { recursive: true })
  if (file.tracked) {
    // Commit a baseline first so the file is tracked, then modify
    // it so `git diff --name-only` (unstaged) shows it. Use
    // `git commit -- <path>` so the commit only takes that one
    // file — a plain `git commit` would also pick up any staged
    // changes from a previous call, which is exactly the trap the
    // multi-file test would otherwise hit.
    await writeFile(full, "baseline\n", "utf8")
    await exec("git", ["add", file.path], { cwd: workDir })
    await exec("git", ["commit", "-m", `baseline ${file.path}`, "-q", "--", file.path], { cwd: workDir })
    await writeFile(full, file.content, "utf8")
    if (file.stage) {
      await exec("git", ["add", file.path], { cwd: workDir })
    }
    return
  }
  await writeFile(full, file.content, "utf8")
  if (file.stage) {
    await exec("git", ["add", file.path], { cwd: workDir })
  }
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
    { ensure: async () => ({ path: workDir, branch: "main", changeDir: null }) } as never,
    connection as never,
    {} as never,
    null,
    workDir,
  )
}

function buildWork(overrides: Partial<WorkItem> = {}): WorkItem {
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
    await dirtyRepoWith({ path: "src/leftover.ts", content: "export const x = 1\n", tracked: true })
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
    await dirtyRepoWith({ path: "src/staged.ts", content: "s\n", tracked: true, stage: true })
    await dirtyRepoWith({ path: "src/unstaged.ts", content: "u\n", tracked: true })
    await dirtyRepoWith({ path: "src/untracked.ts", content: "n\n", untracked: true })

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
    // Agent-backed task leaves a modified file. The cleanup agent
    // commits the file. The next `git status --porcelain` is empty
    // and the task completes with cleanupAttempts=1.
    await dirtyRepoWith({ path: "src/forgot.ts", content: "export const forgot = 1\n", tracked: true })
    setCleanupAgentActionForTest(async (ctx) => {
      const prompt = String(ctx.with?.prompt ?? "")
      // The cleanup prompt must explicitly constrain the agent.
      expect(prompt).toMatch(/do NOT start any new task work/i)
      expect(prompt).toMatch(/do NOT push to any remote/i)
      expect(prompt).toMatch(/commit task-related changes or revert unrelated ones/i)
      expect(prompt).toMatch(/commit SHA|no-change/i)
      expect(prompt).toContain("src/forgot.ts")
      await exec("git", ["add", "src/forgot.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "commit leftover from cleanup test", "-q"], { cwd: ctx.workDir })
      return { status: "success", message: "committed abc1234" }
    })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "agent done" })))

    const result = await executor.execute(buildWork({ uses: "mohist/acp-agent" }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.cleanupAttempts).toBe(1)
    expect(result.message).toBe("agent done")
    const after = await exec("git", ["status", "--porcelain"], { cwd: workDir })
    expect(after.stdout).toBe("")
  })

  it("clearsStaleGitIndexLockBeforeAgentCleanupCommit", async () => {
    // A stale Git index lock is runner control-plane state, not task
    // output. The runner should clear it before asking the agent to
    // perform the bounded cleanup commit so a crashed previous Git
    // command does not make all cleanup retries fail.
    await dirtyRepoWith({ path: "src/stale-lock.ts", content: "export const stale = 1\n", tracked: true })
    const lockPath = join(workDir, ".git", "index.lock")
    await writeFile(lockPath, "", "utf8")
    const old = new Date(Date.now() - 120_000)
    await utimes(lockPath, old, old)

    let cleanupCalls = 0
    setExecutorLockHolderProbeForTest(async () => ({ held: false }))
    setCleanupAgentActionForTest(async (ctx) => {
      cleanupCalls += 1
      await expect(stat(lockPath)).rejects.toMatchObject({ code: "ENOENT" })
      await exec("git", ["add", "src/stale-lock.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "cleanup stale lock test", "-q"], { cwd: ctx.workDir })
      return { status: "success", message: "committed stale lock cleanup" }
    })

    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", message: "agent done" })))

    const result = await executor.execute(buildWork({ uses: "mohist/acp-agent" }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.cleanupAttempts).toBe(1)
    expect(cleanupCalls).toBe(1)
    await expect(stat(lockPath)).rejects.toMatchObject({ code: "ENOENT" })
    const after = await exec("git", ["status", "--porcelain"], { cwd: workDir })
    expect(after.stdout).toBe("")
  })

  it("doesNotClearStaleGitIndexLockWhenStillHeld", async () => {
    await dirtyRepoWith({ path: "src/held-lock.ts", content: "export const held = 1\n", tracked: true })
    const lockPath = join(workDir, ".git", "index.lock")
    await writeFile(lockPath, "", "utf8")
    const old = new Date(Date.now() - 120_000)
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
    await dirtyRepoWith({ path: "src/fresh-lock.ts", content: "export const fresh = 1\n", tracked: true })
    const lockPath = join(workDir, ".git", "index.lock")
    await writeFile(lockPath, "", "utf8")
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
    await dirtyRepoWith({ path: "openspec/changes/issue-127/specs/cli-interface/spec.md", content: "spec\n" })
    let observedExpectedPath: unknown
    setCleanupAgentActionForTest(async (ctx) => {
      const expectInput = ctx.with?.expect
      if (expectInput && typeof expectInput === "object" && !Array.isArray(expectInput)) {
        const files = expectInput.files
        if (Array.isArray(files) && files[0] && typeof files[0] === "object") {
          observedExpectedPath = (files[0] as { path?: unknown }).path
        }
      }
      await exec("git", ["add", "openspec/changes/issue-127/specs/cli-interface/spec.md"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "add spec artifact", "-q"], { cwd: ctx.workDir })
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
        openspecChangeDir: "openspec/changes/issue-127",
      },
    })
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success" })))

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(observedExpectedPath).toBe("openspec/changes/issue-127/specs")
  })

  it("succeedsAfterMultipleCleanupAttemptsWithTotalCountRecorded", async () => {
    // First cleanup only partially resolves the worktree (one
    // file committed, another left). Second cleanup commits the
    // remaining file. The task completes with cleanupAttempts=2.
    await dirtyRepoWith({ path: "src/keep.ts", content: "k\n", tracked: true })
    await dirtyRepoWith({ path: "src/extra.ts", content: "e\n", tracked: true })
    let attempt = 0
    setCleanupAgentActionForTest(async (ctx) => {
      attempt += 1
      const prompt = String(ctx.with?.prompt ?? "")
      expect(prompt).toContain(`attempt ${attempt}`)
      if (attempt === 1) {
        await exec("git", ["add", "src/keep.ts"], { cwd: ctx.workDir })
        await exec("git", ["commit", "-m", "partial cleanup", "-q"], { cwd: ctx.workDir })
        return { status: "success", message: "partial" }
      }
      await exec("git", ["add", "src/extra.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "full cleanup", "-q"], { cwd: ctx.workDir })
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
    await dirtyRepoWith({ path: "src/never.ts", content: "n\n", tracked: true })
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
    await dirtyRepoWith({ path: "src/stubborn.ts", content: "s\n", tracked: true })
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
    await dirtyRepoWith({ path: "src/session.ts", content: "s\n", tracked: true })
    const observedWorkIds: string[] = []
    const observedSessions: (string | undefined)[] = []
    setCleanupAgentActionForTest(async (ctx) => {
      observedWorkIds.push(ctx.workId)
      const session = typeof ctx.with?.["session"] === "string" ? ctx.with["session"] : undefined
      observedSessions.push(session)
      const prompt = String(ctx.with?.prompt ?? "")
      // The runner must NOT add a new session; it must preserve
      // either the explicit `with.session` from the original task
      // or fall back to the workId.
      expect(session === "named-session" || session === undefined).toBe(true)
      expect(ctx.workId).toBe("work-session-test")
      expect(prompt).toContain("attempt 1")
      await exec("git", ["add", "src/session.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "cleanup", "-q"], { cwd: ctx.workDir })
      return { status: "success" }
    })

    const work: WorkItem = {
      workflowRunId: "wf-1",
      workId: "work-session-test",
      workType: "task",
      title: "Session reuse test",
      uses: "mohist/acp-agent",
      with: { session: "named-session", prompt: "do the work" },
      variables: { workspace: { path: workDir, branch: "main", changeDir: null } },
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
    await dirtyRepoWith({ path: "src/default-session.ts", content: "d\n", tracked: true })
    const observedWorkIds: string[] = []
    setCleanupAgentActionForTest(async (ctx) => {
      observedWorkIds.push(ctx.workId)
      expect(ctx.with?.["session"]).toBeUndefined()
      await exec("git", ["add", "src/default-session.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "cleanup", "-q"], { cwd: ctx.workDir })
      return { status: "success" }
    })

    const work: WorkItem = {
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
    await dirtyRepoWith({ path: "src/abort.ts", content: "a\n", tracked: true })
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
    await dirtyRepoWith({ path: "src/preserved.ts", content: "p\n", tracked: true })
    const actionOutput = JSON.stringify({ kind: "acp-agent", acpSessionId: "sess-1", model: "openai/gpt-5" })
    const executor = buildExecutor(makeRegistry(async () => ({ status: "success", output: actionOutput })))

    const result = await executor.execute(buildWork(), new AbortController().signal)

    expect(result.status).toBe("failed")
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence.kind).toBe("dirty-worktree")
    expect(evidence.acpSessionId).toBe("sess-1")
    expect(evidence.model).toBe("openai/gpt-5")
    expect(evidence.unstaged).toEqual(["src/preserved.ts"])
  })

  it("treatsNonGitWorktreeAsClean", async () => {
    // Defensive behaviour: the executor should not crash when the
    // worktree is not a git repository (e.g. test fixtures that
    // resolve to a plain tmpdir). It must treat the worktree as
    // clean so the task can still complete.
    const plainDir = await mkdtemp(join(tmpdir(), "mohist-executor-cleanup-plain-"))
    try {
      const executor = new WorkExecutor(
        makeRegistry(async () => ({ status: "success", message: "ran" })),
        { ensure: async () => ({ path: plainDir, branch: null, changeDir: null }) } as never,
        connection as never,
        {} as never,
        null,
        plainDir,
      )

      const work: WorkItem = {
        workflowRunId: "wf-1",
        workId: "work-plain",
        workType: "task",
        title: "Plain tmpdir test",
        uses: "core/script",
        with: {},
        variables: { workspace: { path: plainDir, branch: null, changeDir: null } },
      }

      const result = await executor.execute(work, new AbortController().signal)

      expect(result.status).toBe("completed")
      expect(result.cleanupAttempts).toBeUndefined()
    } finally {
      await rm(plainDir, { recursive: true, force: true })
    }
  })

  it("cleanupPromptCarriesExplicitConstraintsAndOriginalPromptContext", async () => {
    // The cleanup prompt must name the file categories, instruct
    // the agent to report a commit SHA or no-change, and include
    // a short reference to the original task prompt for context.
    await dirtyRepoWith({ path: "src/context.ts", content: "c\n" })
    let capturedPrompt: string | undefined
    setCleanupAgentActionForTest(async (ctx) => {
      capturedPrompt = String(ctx.with?.prompt ?? "")
      await exec("git", ["add", "src/context.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "context cleanup", "-q"], { cwd: ctx.workDir })
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
    // the legitimate plain-tmpdir case.
    setExecutorGitRunnerForTest(async (_workDir, args) => {
      if (args[0] === "rev-parse" && args[1] === "--is-inside-work-tree") {
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
    expect(result.cleanupAttempts).toBe(0)
    expect(result.message).toMatch(/worktree probe failed/)
    expect(result.output).toBeDefined()
    const evidence = JSON.parse(result.output ?? "{}")
    expect(evidence).toMatchObject({
      kind: "dirty-worktree",
      staged: [],
      unstaged: [],
      untracked: [],
      cleanupAttempts: 0,
    })
  })

  it("cleanupReplacesLoaderSpecPromptWithLiteralCleanupString", async () => {
    // When the original task carries a structured (loader) prompt
    // instead of a literal string, the cleanup loop replaces the
    // prompt with a literal cleanup string. The structured prompt is
    // not preserved into the cleanup follow-up because the executor
    // does not know how to load it; the constraint must therefore be
    // documented so callers don't depend on the loader resolution
    // during cleanup.
    await dirtyRepoWith({ path: "src/loader.ts", content: "l\n" })
    let capturedPrompt: unknown
    setCleanupAgentActionForTest(async (ctx) => {
      capturedPrompt = ctx.with?.prompt
      await exec("git", ["add", "src/loader.ts"], { cwd: ctx.workDir })
      await exec("git", ["commit", "-m", "cleanup", "-q"], { cwd: ctx.workDir })
      return { status: "success" }
    })

    const work: WorkItem = {
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
