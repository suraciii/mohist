import { afterEach, describe, expect, it } from "vitest"
import { mergeAction, mergeReadyAction, setMergeConflictResolverForTest, setMergeGitRunnerForTest } from "../src/actions/registry.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

afterEach(() => {
  setMergeGitRunnerForTest(null)
  setMergeConflictResolverForTest(null)
})

describe("mohist/merge", () => {
  it("SquashMergeConflictWithResolver_ResolvesThenCommitsInWorkspace", async () => {
    const calls: string[] = []
    const workDirs: string[] = []
    let resolverRan = false
    setMergeConflictResolverForTest(async (resolverContext) => {
      resolverRan = true
      expect(resolverContext.workDir).toBe("/fake/worktree")
      expect(resolverContext.workId).toBe("integrate:merge.1-conflict-resolve-1")
      expect(String(resolverContext.with?.prompt)).toContain("specs/web-ui/spec.md")
      return {
        status: "success",
        message: "resolved",
        output: "agent staged resolved files",
      }
    })
    let conflictCheckCount = 0
    setMergeGitRunnerForTest(async (workDir, args) => {
      calls.push(args.join(" "))
      workDirs.push(workDir)
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return fail("CONFLICT (add/add): Merge conflict in specs/web-ui/spec.md")
        case "diff --name-only --diff-filter=U":
          conflictCheckCount++
          return ok(conflictCheckCount === 1 || resolverRan ? "" : "specs/web-ui/spec.md\n")
        case "log --format=* %s master..mo/issue-82":
          return ok("* T-001\n")
        case "commit -m SignalR realtime push (#82) -m * T-001":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-82",
      target: "master",
      strategy: "squash",
      message: "Complete issue #82",
      maxConflictRetries: 1,
      conflictResolver: { with: {} },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).toEqual([
      "status --porcelain",
      "diff --name-only --diff-filter=U",
      "checkout master",
      "merge --squash mo/issue-82",
      "diff --name-only --diff-filter=U",
      "diff --name-only --diff-filter=U",
      "log --format=* %s master..mo/issue-82",
      "commit -m SignalR realtime push (#82) -m * T-001",
      "rev-parse HEAD",
    ])
    expect(workDirs.every((d) => d === "/fake/worktree")).toBe(true)
    expect(output).toMatchObject({
      kind: "merge",
      source: "mo/issue-82",
      target: "master",
      strategy: "squash",
      commit: "abc123",
      conflicts: ["specs/web-ui/spec.md"],
      resolveAttempts: 1,
    })
    expect(output.output).toContain("agent staged resolved files")
    expect(output).not.toHaveProperty("workDir")
  })

  it("SquashMergeConflictWithoutConfiguredResolver_UsesDefaultResolver", async () => {
    let resolverTitle: string | null | undefined
    let resolverRan = false
    setMergeConflictResolverForTest(async (resolverContext) => {
      resolverRan = true
      resolverTitle = resolverContext.title
      return { status: "success", message: "resolved", output: "default resolver completed" }
    })
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return fail("CONFLICT (add/add): Merge conflict in specs/web-ui/spec.md")
        case "diff --name-only --diff-filter=U":
          return ok(resolverRan ? "" : "specs/web-ui/spec.md\n")
        case "log --format=* %s master..mo/issue-82":
          return ok("")
        case "commit -m SignalR realtime push (#82)":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-82",
      target: "master",
      strategy: "squash",
      message: "Complete issue #82",
      maxConflictRetries: 1,
    }))

    expect(result.status).toBe("success")
    expect(resolverTitle).toBe("Resolve merge conflicts")
  })

  it("ExistingMergeConflictOnRetry_ResolvesWithoutStartingANewMerge", async () => {
    const calls: string[] = []
    let resolverRan = false
    setMergeConflictResolverForTest(async () => {
      resolverRan = true
      return { status: "success", message: "resolved", output: "retry resolver completed" }
    })
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "diff --name-only --diff-filter=U":
          return ok(resolverRan ? "" : "specs/web-ui/spec.md\n")
        case "log --format=* %s master..mo/issue-82":
          return ok("")
        case "commit -m SignalR realtime push (#82)":
          return ok("[master abc123] SignalR realtime push (#82)")
        case "rev-parse HEAD":
          return ok("abc123\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-82",
      target: "master",
      strategy: "squash",
      message: "Complete issue #82",
      maxConflictRetries: 1,
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls).not.toContain("checkout master")
    expect(calls).not.toContain("merge --squash mo/issue-82")
    expect(output.output).toContain("Existing merge conflicts detected")
  })

  it("MergeActionFailure_IncludesStructuredDiagnostics", async () => {
    setMergeConflictResolverForTest(async () => ({
      status: "failure",
      message: "resolver failed",
      output: "resolver failed",
      exitCode: 1,
    }))
    setMergeGitRunnerForTest(async (_workDir, args) => {
      switch (args.join(" ")) {
        case "status --porcelain":
          return ok("")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash mo/issue-82":
          return fail("CONFLICT (content): Merge conflict in file.txt")
        case "diff --name-only --diff-filter=U":
          return ok("file.txt\n")
        case "rev-parse master":
          return ok("base-sha\n")
        case "rev-parse mo/issue-82":
          return ok("head-sha\n")
        case "merge-base master mo/issue-82":
          return ok("merge-base-sha\n")
        case "log --format=* %s master..mo/issue-82":
          return ok("")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeAction(context({
      source: "mo/issue-82",
      target: "master",
      strategy: "squash",
      message: "Complete issue #82",
      maxConflictRetries: 1,
      conflictResolver: { with: { agent: { type: "opencode" } } },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "merge",
      source: "mo/issue-82",
      target: "master",
      strategy: "squash",
      targetBranch: "master",
      baseSha: "base-sha",
      candidateHeadSha: "head-sha",
      mergeBaseSha: "merge-base-sha",
      conflicts: ["file.txt"],
      resolveAttempts: 1,
    })
  })
})

describe("mohist/merge-ready", () => {
  it("CleanCandidate_ReturnsStructuredMergeability", async () => {
    const calls: string[] = []
    const workDirs: string[] = []
    setMergeGitRunnerForTest(async (workDir, args) => {
      calls.push(args.join(" "))
      workDirs.push(workDir)
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("head-sha\n")
        case "merge-base master HEAD":
          return ok("merge-base-sha\n")
        case "rev-parse --abbrev-ref HEAD":
          return ok("issue-branch\n")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash --no-commit HEAD":
          return ok("")
        case "reset --hard":
          return ok("HEAD is now at base-sha")
        case "checkout issue-branch":
          return ok("Switched to branch 'issue-branch'")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeReadyAction(context({ baseBranch: "master" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(workDirs.every((d) => d === "/fake/worktree")).toBe(true)
    expect(output).toMatchObject({
      kind: "merge-ready",
      targetBranch: "master",
      strategy: "squash",
      baseSha: "base-sha",
      candidateHeadSha: "head-sha",
      mergeBaseSha: "merge-base-sha",
      canMerge: true,
      conflictFiles: [],
    })
    expect(output.checkedAt).toBeDefined()
  })

  it("ConflictingCandidate_ReturnsConflictFiles", async () => {
    const calls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("head-sha\n")
        case "merge-base master HEAD":
          return ok("merge-base-sha\n")
        case "rev-parse --abbrev-ref HEAD":
          return ok("issue-branch\n")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash --no-commit HEAD":
          return fail("CONFLICT (content): Merge conflict in file.txt")
        case "diff --name-only --diff-filter=U":
          return ok("file.txt\n")
        case "reset --hard":
          return ok("HEAD is now at base-sha")
        case "checkout issue-branch":
          return ok("Switched to branch 'issue-branch'")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const result = await mergeReadyAction(context({ baseBranch: "master" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "merge-ready",
      targetBranch: "master",
      strategy: "squash",
      baseSha: "base-sha",
      candidateHeadSha: "head-sha",
      mergeBaseSha: "merge-base-sha",
      canMerge: false,
      conflictFiles: ["file.txt"],
    })
  })

  it("PreflightRestoresOriginalBranch_AfterTemporaryMerge", async () => {
    const calls: string[] = []
    setMergeGitRunnerForTest(async (_workDir, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse master":
          return ok("base-sha\n")
        case "rev-parse HEAD":
          return ok("head-sha\n")
        case "merge-base master HEAD":
          return ok("merge-base-sha\n")
        case "rev-parse --abbrev-ref HEAD":
          return ok("issue-branch\n")
        case "checkout master":
          return ok("Switched to branch 'master'")
        case "merge --squash --no-commit HEAD":
          return ok("")
        case "reset --hard":
          return ok("HEAD is now at base-sha")
        case "checkout issue-branch":
          return ok("Switched to branch 'issue-branch'")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    await mergeReadyAction(context({ baseBranch: "master" }))

    expect(calls).toContain("checkout master")
    expect(calls[calls.length - 1]).toBe("checkout issue-branch")
  })
})

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:merge.1",
    workType: "task",
    stage: "integrate",
    title: "Merge branch",
    uses: "mohist/merge",
    with: withOverrides,
    variables: {
      repository: { gitUrl: "https://example.com/repo.git", baseBranch: "main" },
      issue: { title: "SignalR realtime push", number: 82 },
      ...variables,
    },
    workDir: "/fake/worktree",
    issueNumber: 82,
    signal: new AbortController().signal,
  }
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}
