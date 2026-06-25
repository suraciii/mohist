import { afterEach, describe, expect, it } from "vitest"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { pushAction, setPushGitRunnerForTest } from "../src/actions/push.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

type GitCall = { workDir: string; args: string[] }

const WORKSPACE_PATH = "/workspace"
const PROJECT_PATH = "/project-checkout"

afterEach(() => {
  setPushGitRunnerForTest(null)
})

function installGit(respond: (call: GitCall, history: GitCall[]) => { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string } | Promise<{ success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }>) {
  const calls: GitCall[] = []
  setPushGitRunnerForTest(async (workDir, args) => {
    const record: GitCall = { workDir, args: [...args] }
    calls.push(record)
    return await respond(record, calls)
  })
  return calls
}

function ok(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string) {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}

function workspaceCalls(calls: GitCall[]) {
  return calls.filter((c) => c.workDir === WORKSPACE_PATH).map((c) => c.args.join(" "))
}

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-push-1",
    workId: "integrate:push.1",
    workType: "task",
    stage: "integrate",
    title: "Push prepared commit",
    uses: "mohist/push",
    with: withOverrides,
    variables: {
      project: { path: WORKSPACE_PATH },
      issue: { title: "Push action issue", number: 99 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "master",
        name: "master",
      },
      workspace: {
        path: WORKSPACE_PATH,
        branch: "mo/issue-99",
        changeDir: null,
      },
      mohist: { runId: "wr-push-1" },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    issueNumber: 99,
    signal: new AbortController().signal,
  }
}

describe("mohist/push", () => {
  it("DefaultRegistry_RegistersPushAction", () => {
    const registry = createDefaultRegistry()

    expect(registry.resolve("mohist/push")).toBe(pushAction)
  })

  it("FastForwardPush_AdvancesRemoteTargetViaRefspec", async () => {
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "push origin mo/issue-99:master":
          return ok("To https://example.com/repo.git\n   base-sha..source-sha  mo/issue-99 -> master")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(workspaceCalls(calls)).toEqual([
      "rev-parse mo/issue-99",
      "push origin mo/issue-99:master",
    ])
    expect(output).toMatchObject({
      kind: "push",
      status: "completed",
      source: "mo/issue-99",
      target: "master",
      remote: "origin",
      refspec: "mo/issue-99:master",
      landedCommit: "source-sha",
      pushed: true,
      failureKind: null,
    })
  })

  it("ProjectPathDiffers_UsesBoundWorkspacePath", async () => {
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "push origin mo/issue-99:master":
          return ok("To https://example.com/repo.git\n   base-sha..source-sha  mo/issue-99 -> master")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context({}, { project: { path: PROJECT_PATH } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(calls.map((call) => call.workDir)).toEqual([WORKSPACE_PATH, WORKSPACE_PATH])
    expect(calls.some((call) => call.workDir === PROJECT_PATH)).toBe(false)
    expect(output.workDir).toBe(WORKSPACE_PATH)
  })

  it("FastForwardPush_NoCheckoutOrCloneOrWorktreeMutation", async () => {
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "push origin mo/issue-99:master":
          return ok("To https://example.com/repo.git\n   base-sha..source-sha  mo/issue-99 -> master")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    await pushAction(context())

    const workspaceCmdSet = new Set(workspaceCalls(calls))
    // No checkout, no clone, no reset, no merge, no commit, no status
    // mutation — push is a ref-only operation.
    for (const forbidden of [
      "checkout master",
      "checkout -B master",
      "checkout -B master origin/master",
      "clone",
      "reset --hard",
      "merge --squash",
      "commit -m",
      "fetch origin master",
      "status --porcelain",
      "merge-base",
      "worktree",
    ]) {
      expect(workspaceCmdSet.has(forbidden)).toBe(false)
    }
  })

  it("NonFastForwardRejection_ClassifiesAsBaseMoved", async () => {
    installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "push origin mo/issue-99:master":
          return fail("To https://example.com/repo.git\n ! [rejected]        master -> master (non-fast-forward)\nerror: failed to push some refs to 'https://example.com/repo.git'\nhint: Updates were rejected because the tip of your current branch is behind\nhint: its remote counterpart. Integrate the remote changes before pushing again.")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "push",
      status: "failed",
      source: "mo/issue-99",
      target: "master",
      remote: "origin",
      landedCommit: "source-sha",
      pushed: false,
      failureKind: "base-moved",
    })
    expect(result.message).toContain("base branch moved")
  })

  it("RejectedWithFetchFirstHint_ClassifiesAsBaseMoved", async () => {
    installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "push origin mo/issue-99:master":
          return fail("To https://example.com/repo.git\n ! [rejected]        master -> master (fetch first)\nerror: failed to push some refs")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.failureKind).toBe("base-moved")
  })

  it("TransientAuthError_ClassifiesAsRetrySafe", async () => {
    installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "push origin mo/issue-99:master":
          return fail("fatal: could not read Username for 'https://example.com': terminal prompts disabled\nfatal: Authentication failed for 'https://example.com/repo.git/'")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "push",
      landedCommit: "source-sha",
      pushed: false,
      failureKind: "retry-safe",
    })
  })

  it("SourceResolveFails_ClassifiesAsRetrySafeWithNullCommit", async () => {
    installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return fail("fatal: ambiguous argument 'mo/issue-99'")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "push",
      landedCommit: null,
      pushed: false,
      failureKind: "retry-safe",
    })
  })

  it("ExplicitRemoteOption_PushesAgainstConfiguredRemote", async () => {
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "push upstream mo/issue-99:master":
          return ok("To upstream\n   base-sha..source-sha  mo/issue-99 -> master")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context({ remote: "upstream" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(workspaceCalls(calls)).toContain("push upstream mo/issue-99:master")
    expect(workspaceCalls(calls)).not.toContain("push origin mo/issue-99:master")
    expect(output).toMatchObject({
      remote: "upstream",
      refspec: "mo/issue-99:master",
      pushed: true,
      failureKind: null,
    })
  })

  it("ExplicitSourceOption_PushesThatRefAsSource", async () => {
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse custom-source":
          return ok("custom-sha\n")
        case "push origin custom-source:master":
          return ok("To https://example.com/repo.git\n   base-sha..custom-sha  custom-source -> master")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context({ source: "custom-source" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(workspaceCalls(calls)).toContain("rev-parse custom-source")
    expect(workspaceCalls(calls)).toContain("push origin custom-source:master")
    expect(output).toMatchObject({
      source: "custom-source",
      landedCommit: "custom-sha",
      pushed: true,
    })
  })

  it("NoLandingClone_CreatesNone", async () => {
    let landingCloneAttempted = false
    installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("source-sha\n")
        case "push origin mo/issue-99:master":
          return ok("To https://example.com/repo.git\n   base-sha..source-sha  mo/issue-99 -> master")
        default:
          if (command.startsWith("clone")) landingCloneAttempted = true
          return fail(`unexpected git call: ${command}`)
      }
    })

    await pushAction(context())

    expect(landingCloneAttempted).toBe(false)
  })

  it("PushResult_RecordsLandedCommitAndPushOccurred", async () => {
    installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("abc123def456\n")
        case "push origin mo/issue-99:master":
          return ok("To https://example.com/repo.git\n   base-sha..abc123def456  mo/issue-99 -> master")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context())
    const output = JSON.parse(result.output ?? "{}")

    // Single push owner: the landed commit and the push-occurred flag both
    // come from this action. Downstream renderers read `landedCommit` and
    // `pushed` directly from the JSON output.
    expect(output.landedCommit).toBe("abc123def456")
    expect(output.pushed).toBe(true)
    expect(result.status).toBe("success")
  })

  it("ForceWithLease_AppendsLeaseFlagToGitPushInvocation", async () => {
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("rewritten-sha\n")
        case "push --force-with-lease origin mo/issue-99:master":
          return ok("To https://example.com/repo.git\n + rewritten-sha...rewritten-sha  mo/issue-99 -> master (forced update)")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context({ forceWithLease: true }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(workspaceCalls(calls)).toEqual([
      "rev-parse mo/issue-99",
      "push --force-with-lease origin mo/issue-99:master",
    ])
    expect(workspaceCalls(calls).some((cmd) => cmd === "push origin mo/issue-99:master")).toBe(false)
    expect(output).toMatchObject({
      forceWithLease: true,
      pushed: true,
      landedCommit: "rewritten-sha",
      failureKind: null,
    })
  })

  it("ForceWithLease_AcceptsTruthyStringAndRejectsAbsent", async () => {
    const calls = installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("a-sha\n")
        case "push --force-with-lease origin mo/issue-99:master":
          return ok("ok\n")
        case "push origin mo/issue-99:master":
          return ok("ok\n")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const stringTrue = await pushAction(context({ forceWithLease: "true" }))
    const stringTrueOutput = JSON.parse(stringTrue.output ?? "{}")
    expect(stringTrue.status).toBe("success")
    expect(stringTrueOutput.forceWithLease).toBe(true)
    expect(workspaceCalls(calls)).toContain("push --force-with-lease origin mo/issue-99:master")

    calls.length = 0
    const absent = await pushAction(context({ forceWithLease: "no" }))
    const absentOutput = JSON.parse(absent.output ?? "{}")
    expect(absent.status).toBe("success")
    expect(absentOutput.forceWithLease).toBe(false)
    expect(workspaceCalls(calls)).toEqual([
      "rev-parse mo/issue-99",
      "push origin mo/issue-99:master",
    ])
    expect(workspaceCalls(calls).some((cmd) => cmd.startsWith("push --force-with-lease"))).toBe(false)
  })

  it("ForceWithLease_RecordsFlagOnNonFastForwardFailure", async () => {
    installGit(async (_call, history) => {
      const command = history[history.length - 1].args.join(" ")
      switch (command) {
        case "rev-parse mo/issue-99":
          return ok("rewritten-sha\n")
        case "push --force-with-lease origin mo/issue-99:master":
          return fail("To https://example.com/repo.git\n ! [rejected]        mo/issue-99 -> mo/issue-99 (stale info)\nerror: failed to push some refs")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })

    const result = await pushAction(context({ forceWithLease: true }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      forceWithLease: true,
      pushed: false,
      failureKind: "base-moved",
    })
  })
})
