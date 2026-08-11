import { describe, expect, it as vitestIt } from "vitest"
import { parseIssueField, resolveIssueFields } from "../src/actions/issue-fields.js"
import type { RunnerFileSystem, RunnerResourceContext } from "../src/system/filesystem.js"
import { withTestRunnerResources } from "./support/test-resources.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"

type IssueFieldCommandRunner = NonNullable<RunnerResourceContext["issueFieldCommandRunner"]>

interface IssueFieldTestResources {
  fileSystem: RunnerFileSystem
  issueFieldCommandRunner?: IssueFieldCommandRunner
}

function it(name: string, body: (resources: IssueFieldTestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: IssueFieldTestResources = { fileSystem: new MemoryFileSystem() }
    await withTestRunnerResources(async () => await body(resources), resources)
  })
}

describe("issue field source parsing", () => {
  it("reads title and body from mo issue view envelope output", () => {
    const json = JSON.stringify({
      success: true,
      data: {
        title: "Use issue title for squash",
        body: "Use issue body for pull request description",
      },
    })

    expect(parseIssueField(json, "issue.title")).toBe("Use issue title for squash")
    expect(parseIssueField(json, "issue.body")).toBe("Use issue body for pull request description")
  })

  it("also accepts direct issue objects", () => {
    const json = JSON.stringify({ title: "Direct title", body: "Direct body" })

    expect(parseIssueField(json, "issue.title")).toBe("Direct title")
    expect(parseIssueField(json, "issue.body")).toBe("Direct body")
  })

  it("uses the current mo issue view command surface", async (resources) => {
    let command: string[] = []
    resources.issueFieldCommandRunner = async (cmd, args) => {
      command = [cmd, ...args]
      return {
        exitCode: 0,
        stdout: JSON.stringify({ title: "Issue title", body: "Issue body" }),
        stderr: "",
      }
    }

    await expect(resolveIssueFields({
      workDir: "/tmp/worktree",
      signal: new AbortController().signal,
      projectId: "proj_1",
      issueNumber: 248,
    })).resolves.toEqual({ title: "Issue title", body: "Issue body" })
    expect(command).toEqual([
      "mo",
      "issue",
      "view",
      "248",
      "--project",
      "proj_1",
      "--json",
      "title,body",
    ])
  })
})
