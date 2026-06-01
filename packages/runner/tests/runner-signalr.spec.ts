import { describe, expect, it } from "vitest"
import { resolveWorkspaceQuery } from "../src/server/runner-signalr.js"

describe("RunnerSignalRClient workspace queries", () => {
  it("WorkspaceQuery_UsesExplicitWorktreeAndBaseBranch", () => {
    const query = resolveWorkspaceQuery({
      issueNumber: 25,
      worktreePath: "/tmp/mohist/worktrees/issue-25",
      branch: "mo/issue-25",
      baseBranch: "master",
    })

    expect(query).toEqual({
      workDir: "/tmp/mohist/worktrees/issue-25",
      baseBranch: "master",
      head: "mo/issue-25",
    })
  })

  it("WorkspaceQuery_RejectsMissingBaseBranchInsteadOfGuessingMain", () => {
    const query = resolveWorkspaceQuery({
      issueNumber: 25,
      worktreePath: "/tmp/mohist/worktrees/issue-25",
    })

    expect(query).toBeNull()
  })
})
