import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { execSync } from "node:child_process"
import { afterEach, beforeAll, describe, expect, it } from "vitest"
import { rebaseAction, setRebaseConflictResolverForTest, setRebaseExistsCheckerForTest } from "../src/actions/rebase.js"
import { pushAction } from "../src/actions/push.js"
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

async function initRepo(path: string) {
  await git(path, "init", "--initial-branch=master")
  await git(path, "config", "user.email", "test@example.com")
  await git(path, "config", "user.name", "Test User")
}

describe("prepare + publish end-to-end", () => {
  it("PublishInProjectRepo_ReadsSharedMoIssueBranchRefAfterPrepareRebaseInWorktree", { timeout: 30_000 }, async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-delivery-shared-ref-"))
    tempDirs.push(root)

    const remote = join(root, "remote.git")
    await mkdir(remote, { recursive: true })
    await git(root, "init", "--bare", remote)

    const repo = join(root, "repo")
    await mkdir(repo, { recursive: true })
    await initRepo(repo)
    await git(repo, "remote", "add", "origin", remote)
    await writeFile(join(repo, "README.md"), "base\n")
    await git(repo, "add", ".")
    await git(repo, "commit", "-m", "base")
    await git(repo, "push", "-u", "origin", "master")

    const workspace = join(root, "workspace")
    await git(repo, "worktree", "add", "-b", "mo/issue-141", workspace, "master")
    await writeFile(join(workspace, "feature-a.txt"), "first issue change\n")
    await git(workspace, "add", ".")
    await git(workspace, "commit", "-m", "issue change 1")
    await writeFile(join(workspace, "feature-b.txt"), "second issue change\n")
    await git(workspace, "add", ".")
    await git(workspace, "commit", "-m", "issue change 2")

    await git(repo, "checkout", "master")
    await writeFile(join(repo, "base-evolution.txt"), "later base\n")
    await git(repo, "add", ".")
    await git(repo, "commit", "-m", "base evolves")
    await git(repo, "push", "origin", "master")

    setRebaseExistsCheckerForTest(() => false)
    setRebaseConflictResolverForTest(async () => ({ status: "success", message: "noop", output: "" }))

    const rebaseContext: ActionContext = {
      workflowRunId: "wr-141",
      workId: "integrate:rebase.1",
      workType: "task",
      stage: "integrate",
      title: "Rebase and squash branch",
      uses: "mohist/rebase",
      with: { baseBranch: "master", remote: "origin", squash: true, message: "Complete issue #141" },
      variables: {
        project: { path: "/project/path" },
        repository: { gitUrl: remote, baseBranch: "master" },
        workspace: { path: workspace, branch: "mo/issue-141", changeDir: null },
        issue: { title: "Split delivery", number: 141 },
      },
      workDir: workspace,
      issueNumber: 141,
      signal: new AbortController().signal,
    }
    const rebaseResult = await rebaseAction(rebaseContext)
    expect(rebaseResult.status).toBe("success")
    const rebaseOutput = JSON.parse(rebaseResult.output ?? "{}")
    expect(rebaseOutput).toMatchObject({ kind: "rebase", squashed: true, failureKind: null })

    const commitCount = (await git(workspace, "rev-list", "--count", "origin/master..HEAD")).stdout.trim()
    expect(commitCount).toBe("1")
    expect((await git(workspace, "log", "-1", "--format=%s")).stdout.trim()).toBe("Complete issue #141")
    expect((await git(workspace, "rev-parse", "--abbrev-ref", "HEAD")).stdout.trim()).toBe("mo/issue-141")

    const pushContext: ActionContext = {
      ...rebaseContext,
      workId: "integrate:push.1",
      title: "Push changes",
      uses: "mohist/push",
      with: { source: "mo/issue-141", target: "master", remote: "origin" },
      workDir: workspace,
      variables: {
        ...rebaseContext.variables,
        project: { path: repo },
      },
    }
    const pushResult = await pushAction(pushContext)
    expect(pushResult.status).toBe("success")
    const pushOutput = JSON.parse(pushResult.output ?? "{}")
    expect(pushOutput).toMatchObject({ kind: "push", pushed: true, failureKind: null, workDir: workspace })

    await git(repo, "fetch", "origin", "master")
    const remoteMasterHead = (await git(root, "--git-dir=" + remote, "rev-parse", "master")).stdout.trim()
    expect(remoteMasterHead).toBe(pushOutput.landedCommit)
    expect((await git(repo, "log", "origin/master", "-1", "--format=%s")).stdout.trim()).toBe("Complete issue #141")
    expect((await git(workspace, "rev-parse", "--abbrev-ref", "HEAD")).stdout.trim()).toBe("mo/issue-141")
  })
})
