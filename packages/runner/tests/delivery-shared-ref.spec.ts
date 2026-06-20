import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { execSync } from "node:child_process"
import { afterEach, beforeAll, describe, expect, it } from "vitest"
import {
  prepareAction,
  publishAction,
  setDeliveryGitRunnerForTest,
  setDeliveryWorkspaceManagerForTest,
} from "../src/actions/registry.js"
import { setRebaseConflictResolverForTest, setRebaseExistsCheckerForTest } from "../src/actions/rebase.js"
import { LandingWorkspaceInfo } from "../src/runtime/workspace.js"
import type { DeliveryWorkspaceManager } from "../src/actions/registry.js"
import { deleteDirectory, ensureDir, runCommand } from "../src/system/process.js"
import type { ActionContext } from "../src/core/types.js"

const tempDirs: string[] = []
let GIT_BIN = "/usr/bin/git"

beforeAll(() => {
  try {
    GIT_BIN = execSync("command -v git", { encoding: "utf8" }).trim() || "/usr/bin/git"
  } catch {
    GIT_BIN = "/usr/bin/git"
  }
})

afterEach(async () => {
  setDeliveryGitRunnerForTest(null)
  setRebaseConflictResolverForTest(null)
  setRebaseExistsCheckerForTest(null)
  setDeliveryWorkspaceManagerForTest(null)
  await Promise.all(tempDirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })))
})

async function git(cwd: string, ...args: string[]) {
  const result = await runCommand(GIT_BIN, args, cwd, new AbortController().signal)
  if (result.exitCode !== 0) {
    throw new Error(`git ${args.join(" ")} failed in ${cwd} (git=${GIT_BIN}): exit=${result.exitCode} stderr=${result.stderr} stdout=${result.stdout}`)
  }
  return result
}

async function gitOk(cwd: string, ...args: string[]) {
  const result = await git(cwd, ...args)
  if (result.exitCode !== 0) {
    throw new Error(`git ${args.join(" ")} failed in ${cwd}: ${result.stderr}`)
  }
  return result
}

async function initRepo(path: string) {
  await gitOk(path, "init", "--initial-branch=master")
  await gitOk(path, "config", "user.email", "test@example.com")
  await gitOk(path, "config", "user.name", "Test User")
}

describe("prepare + publish end-to-end", () => {
  it("PublishInProjectRepo_ReadsSharedMoIssueBranchRefAfterPrepareRebaseInWorktree", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-delivery-shared-ref-"))
    tempDirs.push(root)

    // Set up a bare "remote" so prepare's fetch has an origin to talk to.
    const remote = join(root, "remote.git")
    await mkdir(remote, { recursive: true })
    await gitOk(root, "init", "--bare", remote)

    const repo = join(root, "repo")
    await mkdir(repo, { recursive: true })
    await initRepo(repo)
    await gitOk(repo, "remote", "add", "origin", remote)
    await writeFile(join(repo, "README.md"), "base\n")
    await gitOk(repo, "add", ".")
    await gitOk(repo, "commit", "-m", "base")
    await gitOk(repo, "push", "-u", "origin", "master")
    const baseSha = (await gitOk(repo, "rev-parse", "HEAD")).stdout.trim()

    const worktreePath = join(root, "wt")
    await gitOk(repo, "worktree", "add", "-b", "mo/issue-141", worktreePath, "master")
    await writeFile(join(worktreePath, "feature.txt"), "from issue branch\n")
    await gitOk(worktreePath, "add", ".")
    await gitOk(worktreePath, "commit", "-m", "issue change")

    // Add a second commit to the base branch to force prepare to rebase.
    await gitOk(repo, "checkout", "master")
    await writeFile(join(repo, "base-evolution.txt"), "later base\n")
    await gitOk(repo, "add", ".")
    await gitOk(repo, "commit", "-m", "base evolves")
    await gitOk(repo, "push", "origin", "master")

    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async () => ({ status: "success", message: "noop", output: "" }))

    const worktreeContext: ActionContext = {
      workflowRunId: "wr-141",
      workId: "integrate:prepare.1",
      workType: "task",
      stage: "integrate",
      title: "Prepare branch",
      uses: "mohist/prepare",
      with: { baseBranch: "master" },
      variables: {
        project: { path: repo, baseBranch: "master" },
        issue: { title: "Split delivery", number: 141 },
      },
      workDir: worktreePath,
      issueNumber: 141,
      signal: new AbortController().signal,
    }
    const prepareResult = await prepareAction(worktreeContext)
    expect(prepareResult.status).toBe("success")

    // Verify the rebased commit exists in the project repo's refs (shared refstore).
    const preparedHeadInRepo = (await gitOk(repo, "rev-parse", "mo/issue-141")).stdout.trim()
    const localWorktreeHead = (await gitOk(worktreePath, "rev-parse", "HEAD")).stdout.trim()
    expect(preparedHeadInRepo).toBe(localWorktreeHead)
    expect(preparedHeadInRepo).not.toBe(baseSha)

    // The publish action now lands the commit in an isolated landing
    // workspace, so the workflow workspace (the prepared worktree) must
    // stay on `mo/issue-141`. The publish task needs:
    //   - repository.gitUrl (the landing workspace's push target)
    //   - mohist.runId (used to scope the landing dir)
    //   - workspace.path pointing at the prepared worktree (the source
    //     the landing clone is created from)
    const landingRoot = join(root, "landing")
    await mkdir(landingRoot, { recursive: true })
    const landingManager = new TestLandingWorkspaceManager(landingRoot)
    setDeliveryWorkspaceManagerForTest(landingManager)
    landingManager.recordWorkspacePath(worktreePath)

    const projectContext: ActionContext = {
      ...worktreeContext,
      workId: "integrate:publish.1",
      title: "Publish changes",
      uses: "mohist/publish",
      with: { source: "mo/issue-141", target: "master", message: "Complete issue #141" },
      workDir: repo,
      variables: {
        ...worktreeContext.variables,
        mohist: { runId: "wr-141" },
        repository: {
          ...(worktreeContext.variables?.repository as Record<string, unknown> | undefined ?? {}),
          gitUrl: remote,
          baseBranch: "master",
        },
        workspace: {
          path: worktreePath,
          branch: "mo/issue-141",
          changeDir: null,
        },
      },
    }
    const publishResult = await publishAction(projectContext)
    expect(publishResult.status).toBe("success")
    const output = JSON.parse(publishResult.output ?? "{}")
    expect(output).toMatchObject({
      kind: "publish",
      status: "completed",
      source: "mo/issue-141",
      target: "master",
      pushed: true,
      failureKind: null,
    })
    expect(output.landedCommit).not.toBeNull()

    // Publish now lands the commit in an isolated landing workspace and
    // pushes to the bare remote. `repo` is a separate working clone whose
    // local `master` ref does not auto-advance; fetch from origin so we
    // can read the post-push master head and verify the landed tree.
    await gitOk(repo, "fetch", "origin", "master")
    const masterHead = (await gitOk(repo, "rev-parse", "origin/master")).stdout.trim()
    expect(masterHead).toBe(output.landedCommit)
    expect((await gitOk(repo, "log", "origin/master", "-1", "--format=%s")).stdout.trim()).toContain("Split delivery")

    // The push is verified by reading the ref from the bare remote (the
    // shared-ref assertion the design flags as the most important integration
    // guarantee for prepare→publish).
    const remoteMasterHead = (await gitOk(root, "--git-dir=" + remote, "rev-parse", "master")).stdout.trim()
    expect(remoteMasterHead).toBe(output.landedCommit)

    // The workflow workspace (the prepared worktree) must remain on
    // the run branch `mo/issue-141` after publish — the landing commit
    // was built in the isolated landing workspace, not here.
    const finalWorktreeHead = (await gitOk(worktreePath, "rev-parse", "--abbrev-ref", "HEAD")).stdout.trim()
    expect(finalWorktreeHead).toBe("mo/issue-141")
    // The landing workspace itself was disposed by publishAction.
    const landingEntries = await execNoThrow("ls", ["-1", landingRoot])
    expect(landingEntries.trim().length).toBe(0)
  }, 15_000)
})

class TestLandingWorkspaceManager implements DeliveryWorkspaceManager {
  private workspacePath: string | null = null

  constructor(private readonly landingRoot: string) {}

  recordWorkspacePath(path: string) {
    this.workspacePath = path
  }

  async createLandingWorkspace(work: { variables?: Record<string, unknown> | null; workflowRunId?: string | null }, signal: AbortSignal): Promise<LandingWorkspaceInfo> {
    const variables = (work.variables ?? {}) as Record<string, unknown>
    const repository = (variables.repository as Record<string, unknown> | undefined) ?? {}
    const mohist = (variables.mohist as Record<string, unknown> | undefined) ?? {}
    const baseBranch = typeof repository.baseBranch === "string" ? repository.baseBranch : "master"
    const gitUrl = typeof repository.gitUrl === "string" ? repository.gitUrl : ""
    const runId = typeof mohist.runId === "string" ? mohist.runId : "run"
    const workspacePath = this.workspacePath ?? this.findWorkspacePath(variables)
    if (!workspacePath) throw new Error("TestLandingWorkspaceManager: workspace path not recorded")

    await ensureDir(this.landingRoot)
    const landingPath = join(this.landingRoot, `${runId}-${Date.now()}`)
    const clone = await runCommand(GIT_BIN, ["clone", "--shared", "--no-single-branch", workspacePath, landingPath], ".", signal)
    if (clone.exitCode !== 0) {
      throw new Error(`git clone --shared failed: ${clone.stderr || clone.stdout}`)
    }
    const detach = await runCommand(GIT_BIN, ["-C", landingPath, "checkout", "--detach", "HEAD"], landingPath, signal)
    if (detach.exitCode !== 0) {
      await deleteDirectory(landingPath)
      throw new Error(`git checkout --detach HEAD failed: ${detach.stderr || detach.stdout}`)
    }
    const fetchAll = await runCommand(GIT_BIN, ["-C", landingPath, "fetch", "origin", "+refs/heads/*:refs/heads/*"], landingPath, signal)
    if (fetchAll.exitCode !== 0) {
      await deleteDirectory(landingPath)
      throw new Error(`git fetch refs failed: ${fetchAll.stderr || fetchAll.stdout}`)
    }
    const setUrl = await runCommand(GIT_BIN, ["-C", landingPath, "remote", "set-url", "origin", gitUrl], landingPath, signal)
    if (setUrl.exitCode !== 0) {
      await deleteDirectory(landingPath)
      throw new Error(`git remote set-url failed: ${setUrl.stderr || setUrl.stdout}`)
    }
    return { path: landingPath, runId, runBranch: "mo/issue-141", baseBranch, gitUrl }
  }

  async disposeLandingWorkspace(landing: LandingWorkspaceInfo | string, _signal: AbortSignal) {
    const path = typeof landing === "string" ? landing : landing.path
    if (!path) return { path, disposed: true }
    try {
      await deleteDirectory(path)
      return { path, disposed: true }
    } catch (err) {
      return { path, disposed: false, error: err instanceof Error ? err.message : String(err) }
    }
  }

  private findWorkspacePath(variables: Record<string, unknown>): string | null {
    const workspace = variables.workspace as Record<string, unknown> | undefined
    if (workspace && typeof workspace.path === "string") return workspace.path
    return null
  }
}

async function execNoThrow(command: string, args: string[]): Promise<string> {
  try {
    const result = await runCommand(command, args, ".", new AbortController().signal)
    return result.stdout
  } catch {
    return ""
  }
}
