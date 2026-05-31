import { mkdtemp, readFile, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { describe, expect, it } from "vitest"
import { applyWorkflowAgentDefaultForTest, rebaseAction } from "../src/actions/rebase.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import { runCommand } from "../src/system/process.js"

describe("mohist/rebase", () => {
  it("DirtyWorktreeBeforeRebase_CommitsPendingChangesThenRebases", async () => {
    const root = await mkdtemp(join(tmpdir(), "mohist-rebase-"))
    await git(root, "init")
    await git(root, "config", "user.email", "test@example.com")
    await git(root, "config", "user.name", "Test User")
    await writeFile(join(root, "README.md"), "base\n")
    await git(root, "add", ".")
    await git(root, "commit", "-m", "base")
    await git(root, "branch", "-M", "master")
    await git(root, "checkout", "-b", "issue")
    await writeFile(join(root, "feature.txt"), "issue change\n")

    const result = await rebaseAction(context(root))

    expect(result.status).toBe("success")
    expect(await readFile(join(root, "feature.txt"), "utf8")).toBe("issue change\n")
    const status = await git(root, "status", "--porcelain")
    expect(status.stdout.trim()).toBe("")
    const log = await git(root, "log", "--oneline", "--max-count=1")
    expect(log.stdout).toContain("Prepare rebase onto master")
  })

  it("ConflictResolverWithoutAgentConfig_InheritsWorkflowAgentConfig", () => {
    const withInput: JsonObject = { description: "resolve" }

    applyWorkflowAgentDefaultForTest(withInput, {
      vars: { agent: { type: "opencode", model: "openai/gpt-5.4" } },
    })

    expect(withInput.agent).toEqual({ type: "opencode", model: "openai/gpt-5.4" })
  })
})

function context(workDir: string): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "rebase.1",
    workType: "task",
    stage: "check",
    title: "Rebase onto master",
    uses: "mohist/rebase",
    with: { baseBranch: "master" },
    variables: {},
    workDir,
    signal: new AbortController().signal,
  }
}

async function git(cwd: string, ...args: string[]) {
  const result = await runCommand("git", args, cwd, new AbortController().signal)
  if (result.exitCode !== 0) throw new Error(result.stderr || result.stdout)
  return result
}
