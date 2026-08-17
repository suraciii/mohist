import { describe, expect, it } from "vitest"
import type { DispatchWorkItem } from "../src/core/types.js"
import { branchInvariantViolationFailure, checkBranchStability } from "../src/runtime/branch-stability.js"
import type { RunnerResourceContext } from "../src/system/filesystem.js"
import { StatefulFakeWorktree } from "./support/fake-worktree.js"
import { withTestRunnerResources } from "./support/test-resources.js"

const WORKFLOW_RUN_ID = "wr-branch-stability"
const EXPECTED_BRANCH = "mohist/run-wr-branch-stability"
const OTHER_BRANCH = "feature/other"
const WORKDIR = "/virtual/branch-stability"

function work(): DispatchWorkItem {
  return {
    workflowRunId: WORKFLOW_RUN_ID,
    workId: "task",
    workType: "task",
    stage: "build",
    title: "Build",
    uses: "core/script",
    with: {},
  }
}

function withFake<T>(
  fake: StatefulFakeWorktree,
  body: () => Promise<T>,
  resources: Omit<RunnerResourceContext, "fileSystem"> = {},
): Promise<T> {
  return withTestRunnerResources(
    async () => await body(),
    { gitRunner: fake.gitRunner, workspacePrepareExistsChecker: fake.existsChecker, ...resources },
  )
}

function expectedBranchWork() {
  return { work: work(), expectedBranch: EXPECTED_BRANCH, workDir: WORKDIR, signal: new AbortController().signal }
}

describe("branch stability failure", () => {
  it("reports the invariant through error without placing evidence in output", () => {
    const workItem: DispatchWorkItem = {
      workflowRunId: "wf", workId: "task", workType: "task", stage: "build", title: "Build", uses: "core/script", with: {},
    }
    const result = branchInvariantViolationFailure(workItem, {
      kind: "branch-invariant-violation", boundary: "start", expectedBranch: "mohist/run-wf", observedBranch: "main",
    })
    expect(result).toMatchObject({
      status: "failed",
      error: { code: "branch-invariant-violation" },
    })
    expect(result.output).toBeUndefined()
  })

  it("uses the shared health diagnostic when a message is supplied", () => {
    const workItem: DispatchWorkItem = {
      workflowRunId: "wf", workId: "task", workType: "task", stage: "build", title: "Build", uses: "core/script", with: {},
    }
    const result = branchInvariantViolationFailure(workItem, {
      kind: "branch-invariant-violation",
      boundary: "end",
      expectedBranch: "mohist/run-wf",
      observedBranch: "(detached)",
      observedRef: "abc123",
      message: "workspace health failure: operation=end expectedBranch=mohist/run-wf observedBranch=(detached) observedRef=abc123 dirty=false residual=none",
    })
    expect(result.status).toBe("failed")
    expect(result.error?.code).toBe("branch-invariant-violation")
    expect(result.message).toContain("expectedBranch=mohist/run-wf")
    expect(result.message).toContain("observedRef=abc123")
    expect(result.output).toBeUndefined()
  })
})

describe("checkBranchStability with an expected workspace branch", () => {
  it("accepts a healthy workspace on the expected branch at start and end", async () => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKDIR, { branch: EXPECTED_BRANCH, branches: [EXPECTED_BRANCH] })
    await withFake(fake, async () => {
      const { work: w, expectedBranch, workDir, signal } = expectedBranchWork()
      const start = await checkBranchStability(w, workDir, expectedBranch, "start", signal)
      expect(start.kind).toBe("ok")
      const end = await checkBranchStability(w, workDir, expectedBranch, "end", signal)
      expect(end.kind).toBe("ok")
    })
  })

  it("rejects a detached start before the action is invoked and reports the detached ref", async () => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKDIR, { branch: null, commit: "detached-start-sha", branches: [EXPECTED_BRANCH] })
    await withFake(fake, async () => {
      const { work: w, expectedBranch, workDir, signal } = expectedBranchWork()
      const start = await checkBranchStability(w, workDir, expectedBranch, "start", signal)
      expect(start.kind).toBe("violation")
      if (start.kind !== "violation") return
      expect(start.result.status).toBe("failed")
      expect(start.result.error?.code).toBe("branch-invariant-violation")
      expect(start.result.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
      expect(start.result.message).toContain("observedBranch=(detached)")
      expect(start.result.message).toContain("observedRef=detached-start-sha")
      expect(start.result.output).toBeUndefined()
    })
  })

  it("rejects a mismatched branch at the end boundary after a successful action", async () => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKDIR, { branch: OTHER_BRANCH, branches: [EXPECTED_BRANCH, OTHER_BRANCH] })
    await withFake(fake, async () => {
      const { work: w, expectedBranch, workDir, signal } = expectedBranchWork()
      const end = await checkBranchStability(w, workDir, expectedBranch, "end", signal)
      expect(end.kind).toBe("violation")
      if (end.kind !== "violation") return
      expect(end.result.error?.code).toBe("branch-invariant-violation")
      expect(end.result.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
      expect(end.result.message).toContain(`observedBranch=${OTHER_BRANCH}`)
      expect(end.result.message).toContain(`observedRef=${OTHER_BRANCH}`)
    })
  })

  it("fails closed when the branch probe fails at a boundary", async () => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKDIR, { branch: EXPECTED_BRANCH, branches: [EXPECTED_BRANCH] })
    fake.fail((args) => args.join(" ") === "rev-parse --abbrev-ref HEAD", "fatal: unable to read HEAD")
    await withFake(fake, async () => {
      const { work: w, expectedBranch, workDir, signal } = expectedBranchWork()
      const start = await checkBranchStability(w, workDir, expectedBranch, "start", signal)
      expect(start.kind).toBe("violation")
      if (start.kind !== "violation") return
      expect(start.result.error?.code).toBe("branch-invariant-violation")
      expect(start.result.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
      expect(start.result.message).toContain("probe failed")
      expect(start.result.message).toContain("unable to read HEAD")
    })
  })

  it("treats a non-Git directory with an expected branch as an unverified workspace failure", async () => {
    const fake = new StatefulFakeWorktree()
    // No state configured: every probe fails like a non-Git directory.
    await withFake(fake, async () => {
      const { work: w, expectedBranch, workDir, signal } = expectedBranchWork()
      const start = await checkBranchStability(w, workDir, expectedBranch, "start", signal)
      expect(start.kind).toBe("violation")
      if (start.kind !== "violation") return
      expect(start.result.error?.code).toBe("branch-invariant-violation")
      expect(start.result.message).toContain(`expectedBranch=${EXPECTED_BRANCH}`)
      expect(start.result.message).toContain("probe failed")
    })
  })

  it("rejects residual rebase state at the end boundary after a successful action", async () => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKDIR, {
      branch: EXPECTED_BRANCH,
      branches: [EXPECTED_BRANCH],
      residual: { rebaseMerge: true },
    })
    await withFake(fake, async () => {
      const { work: w, expectedBranch, workDir, signal } = expectedBranchWork()
      const start = await checkBranchStability(w, workDir, expectedBranch, "start", signal)
      // The start boundary must not reject residual state: workspace-prepare
      // runs precisely to repair it before a business task starts.
      expect(start.kind).toBe("ok")
      const end = await checkBranchStability(w, workDir, expectedBranch, "end", signal)
      expect(end.kind).toBe("violation")
      if (end.kind !== "violation") return
      expect(end.result.error?.code).toBe("branch-invariant-violation")
      expect(end.result.message).toContain("residual=rebase")
      expect(end.result.message).toContain("rebase operation in progress")
    })
  })
})

describe("checkBranchStability without an expected workspace branch", () => {
  it("keeps the boundary observational and never fails on detached or non-Git state", async () => {
    const fake = new StatefulFakeWorktree()
    fake.configure(WORKDIR, { branch: null, commit: "detached-observational" })
    await withFake(fake, async () => {
      const { work: w, workDir, signal } = expectedBranchWork()
      const start = await checkBranchStability(w, workDir, null, "start", signal)
      expect(start.kind).toBe("ok")
      const end = await checkBranchStability(w, workDir, null, "end", signal)
      expect(end.kind).toBe("ok")
    })
  })

  it("keeps the boundary observational for a non-Git directory", async () => {
    const fake = new StatefulFakeWorktree()
    await withFake(fake, async () => {
      const { work: w, workDir, signal } = expectedBranchWork()
      const start = await checkBranchStability(w, workDir, null, "start", signal)
      expect(start.kind).toBe("ok")
    })
  })
})
