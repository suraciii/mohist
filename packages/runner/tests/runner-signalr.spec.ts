import { describe, expect, it, vi } from "vitest"
import { isUnderRunnerRoot, normalizeMaterializePayload, resolveWorkspaceQuery, RunnerSignalRClient } from "../src/server/runner-signalr.js"

interface CapturedBuilder {
  url?: string
  handlers: Array<() => void>
}

const builders: CapturedBuilder[] = []

vi.mock("@microsoft/signalr", () => {
  return {
    HubConnectionBuilder: class {
      private _url?: string
      private _handlers: Array<() => void> = []
      withUrl(url: string) {
        this._url = url
        builders.push({ url, handlers: this._handlers })
        return this
      }
      withAutomaticReconnect() {
        return this
      }
      build() {
        return { on: (_evt: string, _h: (...args: unknown[]) => unknown) => this, start: () => Promise.resolve(), stop: () => Promise.resolve() }
      }
    },
  }
})

describe("RunnerSignalRClient workspace queries", () => {
  it("WorkspaceQuery_UsesExplicitWorkspaceAndBaseBranch", () => {
    const query = resolveWorkspaceQuery({
      workspacePath: "/tmp/mohist/workspaces/issue-25",
      branch: "mohist/run-wr-25",
      baseBranch: "master",
    })

    expect(query).toEqual({
      workDir: "/tmp/mohist/workspaces/issue-25",
      baseBranch: "master",
      head: "mohist/run-wr-25",
    })
  })

  it("WorkspaceQuery_RejectsMissingBaseBranchInsteadOfGuessingMain", () => {
    const query = resolveWorkspaceQuery({
      workspacePath: "/tmp/mohist/workspaces/issue-25",
      branch: "mohist/run-wr-25",
    })

    expect(query).toBeNull()
  })

  it("WorkspaceQuery_RejectsMissingHeadInsteadOfFallingBackToMoIssue", () => {
    // The legacy `mo/issue-{N}` worktree branch is no longer materialized by
    // the runner; the dispatch must supply the per-run head ref.
    const query = resolveWorkspaceQuery({
      issueNumber: 25,
      workspacePath: "/tmp/mohist/workspaces/issue-25",
      baseBranch: "master",
    })

    expect(query).toBeNull()
  })

  it("WorkspaceRemoval_OnlyAllowsPathsUnderRunnerRoot", () => {
    expect(isUnderRunnerRoot("/tmp/mohist/projects", "/tmp/mohist/projects/app/workspaces/issue-1")).toBe(true)
    expect(isUnderRunnerRoot("/tmp/mohist/projects", "/tmp/mohist/projects")).toBe(true)
    expect(isUnderRunnerRoot("/tmp/mohist/projects", "/tmp/mohist/other/issue-1")).toBe(false)
  })
})

describe("RunnerSignalRClient handshake", () => {
  it("IncludesBuildGitHashInQueryStringWhenProvided", () => {
    builders.length = 0
    const hash = "abcdef1234567890abcdef1234567890abcdef12"
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", hash)
    const last = builders.at(-1)
    expect(last?.url).toBe(`http://localhost:3456/hubs/runner?runnerId=runner-1&buildGitHash=${hash}`)
  })

  it("OmitsBuildGitHashWhenNull", () => {
    builders.length = 0
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    const last = builders.at(-1)
    expect(last?.url).toBe("http://localhost:3456/hubs/runner?runnerId=runner-1")
  })

  it("OmitsBuildGitHashWhenNotProvided", () => {
    builders.length = 0
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects")
    const last = builders.at(-1)
    expect(last?.url).toBe("http://localhost:3456/hubs/runner?runnerId=runner-1")
  })
})

describe("normalizeMaterializePayload", () => {
  // Regression: WorkDispatch.Variables is `string?` on the C# side, so the
  // SignalR wire format carries `variables` as a JSON-encoded string. The
  // MaterializeWorkspace handler previously passed that string straight to
  // workspaceManager.materialize, where stringAt(... ["repository","gitUrl"])
  // returned undefined (string is not an object) and every retry-time
  // re-materialization threw "Workspace requires repository.gitUrl...".
  const fullVars = {
    issue: { id: "issue_1", number: 212, title: "t", body: "" },
    repository: { name: "master", gitUrl: "https://github.com/x/y.git", baseBranch: "master" },
    project: { id: "proj_1", name: "demo" },
    mohist: { system: "mohist", runId: "wr_1" },
    workspace: { path: "/tmp/ws", branch: "mohist/run-wr_1", changeDir: "openspec/changes/issue-212" },
  }

  it("ParsesStringVariablesIntoObject_SignalRWireFormat", () => {
    const work = normalizeMaterializePayload({
      workflowRunId: "wr_1",
      workId: "T-001.1",
      workType: "task",
      stage: "build",
      variables: JSON.stringify(fullVars),
      with: JSON.stringify({ agent: { type: "opencode" } }),
    })

    expect(work.variables).toEqual(fullVars)
    expect(work.variables).not.toBeTypeOf("string")
    expect(work.with).toEqual({ agent: { type: "opencode" } })
  })

  it("PreservesObjectVariables_AlreadyParsedShape", () => {
    const work = normalizeMaterializePayload({
      workflowRunId: "wr_1",
      workId: "T-001.1",
      workType: "task",
      stage: "build",
      variables: fullVars,
    })

    expect(work.variables).toEqual(fullVars)
  })

  it("ExposesRepositoryAndIssueAtExpectedPaths_AfterStringParse", () => {
    const work = normalizeMaterializePayload({
      workflowRunId: "wr_1",
      workId: "T-001.1",
      workType: "task",
      variables: JSON.stringify(fullVars),
    })

    // These are the exact reads workspace.ts materialize() performs.
    expect((work.variables as Record<string, unknown>)["repository"]).toEqual(fullVars.repository)
    expect((work.variables as Record<string, unknown>)["issue"]).toEqual(fullVars.issue)
  })

  it("RejectsNonObjectPayload", () => {
    expect(() => normalizeMaterializePayload(null)).toThrow()
    expect(() => normalizeMaterializePayload("not-an-object")).toThrow()
    expect(() => normalizeMaterializePayload([])).toThrow()
  })
})
