import { mkdir, readFile, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, describe, expect, it, vi } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest, type AcpProcessHandle } from "../src/actions/acp-agent.js"
import type { ActionContext } from "../src/core/types.js"
import { runCommand } from "../src/system/process.js"
import { fakeAcpProcess } from "./support/fake-acp.js"
import { createTestTempDir } from "./support/temp-dir.js"

afterEach(() => setAcpProcessFactoryForTest(null))

describe("mohist/acp-agent tool noise cleanup", () => {
  it("AgentMutatesOpencodeLockfile_ActionRestoresToolNoiseBeforeVerification", async () => {
    const warningSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
    try {
      const root = await createTestTempDir("mohist-acp-noise-")
      await git(root, "init")
      await git(root, "config", "user.email", "test@example.com")
      await git(root, "config", "user.name", "Test User")
      await writeFile(join(root, "README.md"), "base\n")
      await mkdir(join(root, ".opencode"), { recursive: true })
      await writeFile(join(root, ".opencode", "package-lock.json"), "locked\n")
      await git(root, "add", ".")
      await git(root, "commit", "-m", "base")

      setAcpProcessFactoryForTest(() => fakeAcpProcess(async () => {
        await writeFile(join(root, ".opencode", "package-lock.json"), "mutated\n")
        await writeFile(join(root, "artifact.txt"), "done\n")
      }))

      const result = await acpAgentAction(context(root))

      expect(result.status).toBe("success")
      expect(await readFile(join(root, ".opencode", "package-lock.json"), "utf8")).toBe("locked\n")
      const status = await git(root, "status", "--short")
      expect(status.stdout.trim()).toBe("?? artifact.txt")
      expect(warningSpy).toHaveBeenCalledTimes(1)
      expect(warningSpy).toHaveBeenNthCalledWith(
        1,
        "mohist acp model not configured; using provider default",
        {
          workflowRunId: "workflow-1",
          workId: "proposal.1",
          stage: "plan",
          sessionName: "proposal.1",
          requestedModel: null,
          requestedModelSource: "none",
        },
      )
    } finally {
      warningSpy.mockRestore()
    }
  })
})

function context(workDir: string): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "proposal.1",
    workType: "task",
    stage: "plan",
    title: "Generate proposal",
    uses: "mohist/acp-agent",
    with: { prompt: "create artifact", expect: { files: [{ path: "artifact.txt" }] } },
    variables: {},
    workDir,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

async function git(cwd: string, ...args: string[]) {
  const result = await runCommand("git", args, cwd, new AbortController().signal)
  if (result.exitCode !== 0) throw new Error(result.stderr || result.stdout)
  return result
}
