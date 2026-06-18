import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { execSync } from "node:child_process"
import { afterEach, beforeAll, describe, expect, it } from "vitest"
import { prepareAction, publishAction, setDeliveryGitRunnerForTest } from "../src/actions/registry.js"
import { setRebaseConflictResolverForTest, setRebaseExistsCheckerForTest } from "../src/actions/rebase.js"
import { runCommand } from "../src/system/process.js"
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

    const projectContext: ActionContext = {
      ...worktreeContext,
      workId: "integrate:publish.1",
      title: "Publish changes",
      uses: "mohist/publish",
      with: { source: "mo/issue-141", target: "master", message: "Complete issue #141" },
      workDir: repo,
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

    const masterHead = (await gitOk(repo, "rev-parse", "master")).stdout.trim()
    expect(masterHead).toBe(output.landedCommit)
    expect((await gitOk(repo, "log", "-1", "--format=%s")).stdout.trim()).toContain("Split delivery")

    // The push is verified by reading the ref from the bare remote (the
    // shared-ref assertion the design flags as the most important integration
    // guarantee for prepare→publish).
    const remoteMasterHead = (await gitOk(root, "--git-dir=" + remote, "rev-parse", "master")).stdout.trim()
    expect(remoteMasterHead).toBe(output.landedCommit)
  })
})
