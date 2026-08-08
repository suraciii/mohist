import { basename, join } from "node:path"
import { AsyncLocalStorage } from "node:async_hooks"
import { describe, expect, it as vitestIt } from "vitest"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"
import { issueWorkspacePath, WorkspaceManager, slugify } from "../src/runtime/workspace.js"
import { WorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import { DefaultCleanupRunner } from "../src/runtime/cleanup-loop.js"
import type { WorkspaceRegistryEntry } from "../src/runtime/workspace-registry.js"
import type { CommandLineOptions, CommandResult } from "../src/system/process.js"
import { currentRunnerFileSystem } from "../src/system/filesystem.js"
import { createTestTempDir } from "./support/temp-dir.js"
import { MemoryDirectoryHandleFileSystem, MemoryFileSystem } from "./support/memory-filesystem.js"
import { withTestRunnerResources } from "./support/test-resources.js"

interface CommandCall {
  command: string
  args: string[]
  cwd: string
}

class FakeGitRunner {
  readonly calls: CommandCall[] = []
  readonly remoteBranches = new Set(["master", "issue-symlink", "issue-parent-swap", "issue-mismatch", "issue-recover-registry"])
  readonly remoteUrl = "https://example.test/mohist.git"
  cloneResult: CommandResult | null = null
  lsRemoteResult: CommandResult | null = null
  failedCheckouts = 0
  beforeClone: (() => Promise<void>) | null = null
  beforeRemote: (() => Promise<void>) | null = null
  private readonly branches = new Map<string, Set<string>>()

  async run(
    command: string,
    args: string[],
    cwd: string,
    _signal: AbortSignal,
    _env?: NodeJS.ProcessEnv,
    _options?: CommandLineOptions,
  ): Promise<CommandResult> {
    this.calls.push({ command, args: [...args], cwd })
    if (command !== "git") throw new Error(`Unexpected command: ${command}`)

    if (args[0] === "ls-remote") {
      if (this.lsRemoteResult) return this.lsRemoteResult
      const branch = args.at(-1)
      return this.remoteBranches.has(branch ?? "")
        ? commandResult(0, `fake-sha\trefs/heads/${branch}\n`)
        : commandResult(0)
    }

    if (args[0] === "clone") {
      if (this.beforeClone) {
        const beforeClone = this.beforeClone
        this.beforeClone = null
        await beforeClone()
      }
      const workspacePath = args[2]
      if (!workspacePath) throw new Error("git clone needs a destination")
      if (this.cloneResult) {
        await currentRunnerFileSystem().ensureDir(workspacePath)
        return this.cloneResult
      }
      await currentRunnerFileSystem().ensureDir(join(workspacePath, ".git", "info"))
      await currentRunnerFileSystem().writeText(join(workspacePath, "README.md"), "base\n")
      this.branches.set(workspacePath, new Set())
      return commandResult(0)
    }

    if (args[0] !== "-C" || !args[1]) throw new Error(`Unexpected git arguments: ${args.join(" ")}`)
    const workspacePath = args[1]
    const gitArgs = args.slice(2)
    let branches = this.branches.get(workspacePath)
    if (!branches) {
      if (!currentRunnerFileSystem().exists(join(workspacePath, ".git"))) throw new Error(`Unknown fake workspace: ${workspacePath}`)
      branches = new Set<string>()
      this.branches.set(workspacePath, branches)
    }

    if (gitArgs[0] === "remote" && gitArgs[1] === "get-url" && gitArgs[2] === "origin") {
      if (this.beforeRemote) {
        const beforeRemote = this.beforeRemote
        this.beforeRemote = null
        await beforeRemote()
      }
      return commandResult(0, `${this.remoteUrl}\n`)
    }

    if (gitArgs[0] === "rev-parse" && gitArgs[1] === "--verify") {
      const branch = gitArgs[2]?.replace("refs/heads/", "")
      return commandResult(0, "fake-sha\n")
    }

    if (gitArgs[0] === "show-ref" && gitArgs[1] === "--verify" && gitArgs[2] === "--quiet") {
      const remoteBranch = gitArgs[3]?.replace("refs/remotes/origin/", "")
      return this.remoteBranches.has(remoteBranch ?? "") ? commandResult(0) : commandResult(1)
    }

    if (gitArgs[0] === "checkout" && (gitArgs[1] === "-b" || gitArgs[1] === "-B")) {
      const branch = gitArgs[2]
      if (!branch) throw new Error("git checkout -b needs a branch")
      branches.add(branch)
      return commandResult(0)
    }

    if (gitArgs[0] === "checkout") {
      if (this.failedCheckouts > 0) {
        this.failedCheckouts -= 1
        return commandResult(1, "", "checkout blocked by unfinished rebase")
      }
      return commandResult(0)
    }

    if (gitArgs[0] === "rebase" || gitArgs[0] === "merge" || gitArgs[0] === "cherry-pick" || gitArgs[0] === "reset") {
      return commandResult(0)
    }

    throw new Error(`Unexpected git arguments: ${args.join(" ")}`)
  }

  commandArgs() {
    return this.calls.map((call) => call.args)
  }
}

interface WorkspaceTestContext {
  fileSystem: MemoryFileSystem
  gitRunner: FakeGitRunner
}

const workspaceTestContext = new AsyncLocalStorage<WorkspaceTestContext>()

function workspaceContext(): WorkspaceTestContext {
  const context = workspaceTestContext.getStore()
  if (!context) throw new Error("workspace test resource context is not active")
  return context
}

const fileSystem = new Proxy({} as MemoryFileSystem, {
  get(_target, property) {
    const value = Reflect.get(workspaceContext().fileSystem, property, workspaceContext().fileSystem)
    return typeof value === "function" ? value.bind(workspaceContext().fileSystem) : value
  },
})

const gitRunner = new Proxy({} as FakeGitRunner, {
  get(_target, property) {
    const value = Reflect.get(workspaceContext().gitRunner, property, workspaceContext().gitRunner)
    return typeof value === "function" ? value.bind(workspaceContext().gitRunner) : value
  },
  set(_target, property, value) {
    const runner = workspaceContext().gitRunner
    Reflect.set(runner, property, value, runner)
    return true
  },
})

async function withWorkspaceTestResources(body: () => unknown): Promise<void> {
  const context: WorkspaceTestContext = {
    fileSystem: new MemoryFileSystem(),
    gitRunner: new FakeGitRunner(),
  }
  const commandRunner = {
    run: (command: string, args: string[], cwd: string, signal: AbortSignal, env?: NodeJS.ProcessEnv, options?: unknown) => context.gitRunner.run(command, args, cwd, signal, env, options as CommandLineOptions | undefined),
  }
  await withTestRunnerResources(async () => {
    await workspaceTestContext.run(context, async () => await body())
  }, { commandRunner, fileSystem: context.fileSystem })
}

async function withWorkspaceFileSystem<T>(fileSystem: MemoryFileSystem, body: () => Promise<T>): Promise<T> {
  const current = workspaceContext()
  const commandRunner = {
    run: (command: string, args: string[], cwd: string, signal: AbortSignal, env?: NodeJS.ProcessEnv, options?: unknown) => current.gitRunner.run(command, args, cwd, signal, env, options as CommandLineOptions | undefined),
  }
  return await withTestRunnerResources(async () =>
    await workspaceTestContext.run({ fileSystem, gitRunner: current.gitRunner }, body),
    { commandRunner, fileSystem })
}

const it = Object.assign(
  (name: string, body: () => unknown) => vitestIt(name, () => withWorkspaceTestResources(body)),
  { each: vitestIt.each.bind(vitestIt) },
) as typeof vitestIt

function commandResult(exitCode = 0, stdout = "", stderr = ""): CommandResult {
  return { exitCode, stdout, stderr }
}

describe("WorkspaceManager.prepare", () => {
  it("FreshRun_CreatesRunBranchAndWorkspaceMarker", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)

    const workspace = await manager.prepare(work("wr-1"), new AbortController().signal)

    const expectedPath = issueWorkspacePath(runnerRoot, "wr-1")
    expect(workspace).toEqual({
      path: expectedPath,
      branch: "mohist/run-wr-1",
    })
    expect(gitRunner.commandArgs()).toContainEqual(["ls-remote", "--heads", "https://example.test/mohist.git", "master"])
    expect(gitRunner.commandArgs()).toContainEqual(["clone", "https://example.test/mohist.git", managedPath(`${expectedPath}.preparing`)])
    expect(gitRunner.commandArgs()).toContainEqual(["-C", managedPath(`${expectedPath}.preparing`), "checkout", "-B", "mohist/run-wr-1", "origin/master"])
    expect(await fileSystem.readText(join(workspace.path, ".mohist", "workspace.json"))).toBe(JSON.stringify({
      workflowRunId: "wr-1",
      runBranch: "mohist/run-wr-1",
    }, null, 2))
  })

  it("MissingWorkspace_RestoresExistingRemoteRunBranch", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const runnerRoot = join(root, "runner")
    gitRunner.remoteBranches.add("mohist/run-wr-restore")

    await new WorkspaceManager(runnerRoot).prepare(work("wr-restore"), new AbortController().signal)

    const workspacePath = issueWorkspacePath(runnerRoot, "wr-restore")
    expect(gitRunner.commandArgs()).toContainEqual([
      "-C",
      managedPath(`${workspacePath}.preparing`),
      "checkout",
      "-B",
      "mohist/run-wr-restore",
      "origin/mohist/run-wr-restore",
    ])
  })

  it("DirectoryHandlePath_UsesTheInjectedDirectoryHandle", async () => {
    await withWorkspaceFileSystem(new MemoryDirectoryHandleFileSystem(), async () => {
      const root = await createTestTempDir("mohist-workspace-")
      const runnerRoot = join(root, "runner")
      await new WorkspaceManager(runnerRoot).prepare(work("wr-child-path"), new AbortController().signal)

      const clone = gitRunner.commandArgs().find((args) => args[0] === "clone")
      expect(clone?.[2]).toMatch(/\/memory-handle-\d+\/wr-child-path\.preparing$/)
    })
  })

  it("SameRunReentry_ReusesWorkspaceWithoutRecloning", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const manager = new WorkspaceManager(join(root, "runner"))
    const item = work("wr-1")

    const first = await manager.prepare(item, new AbortController().signal)
    await fileSystem.writeText(join(first.path, "draft.txt"), "draft\n")
    gitRunner.calls.length = 0

    const second = await manager.prepare(item, new AbortController().signal)

    expect(second.path).toBe(first.path)
    expect(await fileSystem.readText(join(second.path, "draft.txt"))).toBe("draft\n")
    expect(gitRunner.commandArgs()).not.toContainEqual(["clone", "https://example.test/mohist.git", first.path])
    expect(gitRunner.commandArgs()).toContainEqual(["-C", managedPath(first.path), "checkout", "mohist/run-wr-1"])
  })

  it("RestartWithNewRun_UsesADistinctRunWorkspace", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const manager = new WorkspaceManager(join(root, "runner"))

    const first = await manager.prepare(work("wr-old"), new AbortController().signal)
    await fileSystem.writeText(join(first.path, "stale.txt"), "old run data\n")
    gitRunner.calls.length = 0

    const second = await manager.prepare(work("wr-new"), new AbortController().signal)

    expect(second.path).not.toBe(first.path)
    expect(fileSystem.exists(join(first.path, "stale.txt"))).toBe(true)
    expect(second.branch).toBe("mohist/run-wr-new")
    expect(gitRunner.commandArgs()).toContainEqual(["clone", "https://example.test/mohist.git", managedPath(`${second.path}.preparing`)])
  })

  it("MissingBaseBranch_FailsBeforeClone", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const manager = new WorkspaceManager(join(root, "runner"))
    const item = work("wr-1", "does-not-exist")

    await expect(manager.prepare(item, new AbortController().signal)).rejects.toThrow(/cannot be resolved/)

    expect(gitRunner.commandArgs()).not.toContainEqual(expect.arrayContaining(["clone"]))
    expect(fileSystem.exists(join(root, "runner", "workspaces"))).toBe(false)
  })

  it("CloneFailure_IsFatalAndDropsPartialWorkspace", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    gitRunner.cloneResult = commandResult(1, "", "remote unavailable")

    await expect(manager.prepare(work("wr-first"), new AbortController().signal)).rejects.toThrow(/git clone failed/)

    expect(fileSystem.exists(issueWorkspacePath(runnerRoot, "wr-first"))).toBe(false)
  })

  it("BaseBranchLsRemoteTimeout_FailsBeforeCloneWithStructuredStep", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const manager = new WorkspaceManager(join(root, "runner"))
    gitRunner.lsRemoteResult = {
      exitCode: 124,
      stdout: "",
      stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
      status: "timeout",
      timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
    }

    await expect(manager.prepare(work("wr-timeout"), new AbortController().signal)).rejects.toMatchObject({
      kind: "workspace-network-timeout",
      step: {
        name: "git-ls-remote",
        command: "ls-remote --heads https://example.test/mohist.git master",
        exitCode: 124,
        status: "timeout",
        timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
      },
    })
    expect(gitRunner.commandArgs().some((args) => args[0] === "clone")).toBe(false)
  })

  it("CloneTimeout_FailsWithStructuredStep", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const runnerRoot = join(root, "runner")
    const manager = new WorkspaceManager(runnerRoot)
    gitRunner.cloneResult = {
      exitCode: 124,
      stdout: "",
      stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
      status: "timeout",
      timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
    }

    await expect(manager.prepare(work("wr-timeout"), new AbortController().signal)).rejects.toMatchObject({
      name: "WorkspaceNetworkTimeoutError",
      step: {
        name: "git-clone",
        command: expect.stringMatching(new RegExp(`^clone https://example\\.test/mohist\\.git ${managedPathPattern(`${issueWorkspacePath(runnerRoot, "wr-timeout")}.preparing`)}$`)),
        exitCode: 124,
        status: "timeout",
        timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
      },
    })
  })

  it("Preparation_DoesNotUseGitWorktreeCommands", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const manager = new WorkspaceManager(join(root, "runner"))

    await manager.prepare(work("wr-1"), new AbortController().signal)

    expect(gitRunner.commandArgs().filter((args) => args.includes("worktree"))).toEqual([])
  })

  it("ServerSuppliedPath_IsIgnoredForIssueRuns", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const runnerRoot = join(root, "runner")
    const suppliedWorkspacePath = join(runnerRoot, "supplied", "workspaces", "issue-9")
    const item = work("wr-supplied")
    ;(item.variables as Record<string, unknown>).workspace = { path: suppliedWorkspacePath }
    const manager = new WorkspaceManager(runnerRoot)

    const result = await manager.prepare(item, new AbortController().signal)

    expect(result.path).toBe(issueWorkspacePath(runnerRoot, "wr-supplied"))
    expect(result.branch).toBe("mohist/run-wr-supplied")
    expect(gitRunner.commandArgs()).toContainEqual(["clone", "https://example.test/mohist.git", managedPath(`${result.path}.preparing`)])
  })

  it("SymlinkedWorkspaceParent_IsRejectedBeforeClone", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const runnerRoot = join(root, "runner")
    const outside = join(root, "outside")
    await fileSystem.ensureDir(outside)
    await fileSystem.ensureDir(runnerRoot)
    await fileSystem.symlink(outside, join(runnerRoot, "workspaces"))
    const manager = new WorkspaceManager(runnerRoot)

    await expect(manager.prepare(work("wr-symlink", "issue-symlink"), new AbortController().signal)).rejects.toMatchObject({ kind: "workspace-identity-mismatch" })
    expect(gitRunner.commandArgs().some((args) => args[0] === "clone")).toBe(false)
  })

  it("WorkspaceParentReplacement_CloneRemainsInsideVerifiedDirectory", async () => {
    await withWorkspaceFileSystem(new MemoryDirectoryHandleFileSystem(), async () => {
      const root = await createTestTempDir("mohist-workspace-")
      const runnerRoot = join(root, "runner")
      const workspaces = join(runnerRoot, "workspaces")
      const heldWorkspaces = join(runnerRoot, "workspaces-held")
      const outside = join(root, "outside")
      await fileSystem.ensureDir(outside)
      gitRunner.beforeClone = async () => {
        await fileSystem.rename(workspaces, heldWorkspaces)
        await fileSystem.symlink(outside, workspaces)
      }

      await new WorkspaceManager(runnerRoot).prepare(work("wr-parent-swap", "issue-parent-swap"), new AbortController().signal)

      const publicPath = issueWorkspacePath(runnerRoot, "wr-parent-swap")
      expect(fileSystem.exists(join(outside, basename(publicPath)))).toBe(false)
      expect(fileSystem.exists(join(heldWorkspaces, basename(publicPath)))).toBe(true)
      const clone = gitRunner.commandArgs().find((args) => args[0] === "clone")
      expect(clone?.[2]).toMatch(/\/memory-handle-\d+\/wr-parent-swap\.preparing$/)
    })
  })

  it("ExistingMarkerMismatch_IsRejectedBeforeBranchMutation", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const manager = new WorkspaceManager(join(root, "runner"))
    const item = work("wr-mismatch", "issue-mismatch")
    const first = await manager.prepare(item, new AbortController().signal)
    await fileSystem.writeText(join(first.path, ".mohist", "workspace.json"), "{}")
    gitRunner.calls.length = 0

    await expect(manager.prepare(item, new AbortController().signal)).rejects.toMatchObject({ kind: "workspace-identity-mismatch" })
    expect(gitRunner.commandArgs()).toEqual([])
  })

  it("RegistryFailureAfterAtomicRename_IsRecoveredOnRetry", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const register = registry.register.bind(registry)
    let fail = true
    registry.register = async (input) => {
      if (fail) {
        fail = false
        throw new Error("registry interrupted")
      }
      return register(input)
    }
    const manager = new WorkspaceManager(runnerRoot, registry)
    const item = work("wr-recover-registry", "issue-recover-registry")

    await expect(manager.prepare(item, new AbortController().signal)).rejects.toThrow("registry interrupted")
    const path = issueWorkspacePath(runnerRoot, item.workflowRunId)
    expect(fileSystem.exists(path)).toBe(true)

    await manager.prepare(item, new AbortController().signal)
    expect(registry.get(item.workflowRunId)).toMatchObject({ workspacePath: path, workflowRunId: item.workflowRunId })
  })
})

describe("WorkspaceManager.prepare recovery", () => {
  it("FailedCheckout_AbortsResidualOperationAndResetsRunBranch", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const manager = new WorkspaceManager(join(root, "runner"))
    const item = work("wr-recover")
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0
    gitRunner.failedCheckouts = 1

    const recovered = await manager.prepare(item, new AbortController().signal)

    expect(recovered).toMatchObject({ path: workspace.path, branch: "mohist/run-wr-recover" })
    expect(gitRunner.commandArgs()).toContainEqual(["-C", managedPath(workspace.path), "rebase", "--abort"])
    expect(gitRunner.commandArgs()).toContainEqual(["-C", managedPath(workspace.path), "merge", "--abort"])
    expect(gitRunner.commandArgs()).toContainEqual(["-C", managedPath(workspace.path), "cherry-pick", "--abort"])
    expect(gitRunner.commandArgs()).toContainEqual(["-C", managedPath(workspace.path), "reset", "--hard", "mohist/run-wr-recover"])
    expect(gitRunner.commandArgs().some((args) => args[0] === "clone")).toBe(false)
  })

  it("CleanReentry_OnlyChecksOutRunBranch", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const manager = new WorkspaceManager(join(root, "runner"))
    const item = work("wr-clean")
    const workspace = await manager.prepare(item, new AbortController().signal)
    gitRunner.calls.length = 0

    const second = await manager.prepare(item, new AbortController().signal)

    expect(second).toMatchObject({ path: workspace.path, branch: "mohist/run-wr-clean" })
    expect(gitRunner.commandArgs()).toEqual([
      ["-C", managedPath(workspace.path), "remote", "get-url", "origin"],
      ["-C", managedPath(workspace.path), "rev-parse", "--verify", "refs/heads/mohist/run-wr-clean"],
      ["-C", managedPath(workspace.path), "checkout", "mohist/run-wr-clean"],
    ])
  })
})

describe("DefaultCleanupRunner", () => {
  it("WorkspaceParentReplacement_ValidationAndDeleteRemainInsideVerifiedDirectory", async () => {
    const root = await createTestTempDir("mohist-workspace-")
    const runnerRoot = join(root, "runner")
    const workflowRunId = "wr-cleanup-parent-swap"
    const workspacePath = issueWorkspacePath(runnerRoot, workflowRunId)
    const workspaces = join(runnerRoot, "workspaces")
    const heldWorkspaces = join(runnerRoot, "workspaces-held")
    const outside = join(root, "outside")
    const entry = cleanupEntry(workspacePath, workflowRunId)
    await fileSystem.ensureDir(join(workspacePath, ".mohist"))
    await fileSystem.ensureDir(join(workspacePath, ".git"))
    await fileSystem.writeText(join(workspacePath, ".mohist", "workspace.json"), JSON.stringify({
      workflowRunId,
      runBranch: entry.runBranch,
    }))
    await fileSystem.ensureDir(outside)
    let swapped = false
    gitRunner.beforeRemote = async () => {
      if (!swapped) {
        swapped = true
        await fileSystem.rename(workspaces, heldWorkspaces)
        await fileSystem.symlink(outside, workspaces)
      }
    }

    const removed = await new DefaultCleanupRunner(runnerRoot).validateAndDeleteWorkspace(entry)

    expect(removed).toBe(true)
    expect(fileSystem.exists(join(outside, basename(workspacePath)))).toBe(false)
    expect(fileSystem.exists(join(heldWorkspaces, basename(workspacePath)))).toBe(false)
  })
})

describe("WorkspaceManager.slugify", () => {
  it.each([
    ["my-project", "my-project"],
    ["My Project!", "my-project"],
    ["  spaced  out  ", "spaced-out"],
    ["foo_bar.baz", "foo-bar-baz"],
    ["Café", "caf"],
    ["测试-project", "project"],
    ["", "project"],
  ])("slugify(%j) === %j", (input, expected) => {
    expect(slugify(input)).toBe(expected)
  })
})

function work(workflowRunId: string, baseBranch = "master") {
  return {
    workflowRunId,
    workId: "proposal.1",
    workType: "task",
    uses: "mohist/opencode",
    variables: {
      issue: { number: 9 },
      repository: { name: "master", gitUrl: "https://example.test/mohist.git", baseBranch },
    },
  }
}

function managedPath(path: string) {
  return path
}

function managedPathPattern(path: string) {
  return path.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")
}

function cleanupEntry(workspacePath: string, workflowRunId: string): WorkspaceRegistryEntry {
  return {
    issueNumber: 9,
    workflowRunId,
    workspacePath,
    runBranch: `mohist/run-${workflowRunId}`,
    phase: "eligible",
    materializedAt: "2026-01-01T00:00:00.000Z",
    terminalAt: "2026-01-02T00:00:00.000Z",
  }
}
