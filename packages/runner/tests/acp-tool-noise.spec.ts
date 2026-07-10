import { mkdir, readFile, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, describe, expect, it, vi } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../src/actions/acp-agent.js"
import type { ActionContext } from "../src/core/types.js"
import * as processModule from "../src/system/process.js"
import { fakeAcpProcess } from "./support/fake-acp.js"
import { createTestTempDir } from "./support/temp-dir.js"

let restoreProcessRunner: (() => void) | null = null

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  restoreProcessRunner?.()
  restoreProcessRunner = null
})

describe("mohist/acp-agent tool noise cleanup", () => {
  it("AgentMutatesOpencodeLockfile_ActionRestoresToolNoiseBeforeVerification", async () => {
    const warningSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
    try {
      const root = await createTestTempDir("mohist-acp-noise-")
      await writeFile(join(root, "README.md"), "base\n")
      await mkdir(join(root, ".opencode"), { recursive: true })
      const lockfile = join(root, ".opencode", "package-lock.json")
      await writeFile(lockfile, "locked\n")
      const artifact = join(root, "artifact.txt")
      const calls: Array<{ command: string; args: string[]; workDir: string }> = []
      const missingToolFiles: string[] = []
      const runner = vi.spyOn(processModule, "runCommand").mockImplementation(async (command, args, workDir) => {
        calls.push({ command, args: [...args], workDir })
        if (args.join(" ") === "checkout -- .opencode/package-lock.json") {
          await writeFile(lockfile, "locked\n")
          return { exitCode: 0, stdout: "", stderr: "" }
        }
        missingToolFiles.push(args.at(-1) ?? "")
        return { exitCode: 1, stdout: "", stderr: "path is not tracked" }
      })
      restoreProcessRunner = () => runner.mockRestore()

      setAcpProcessFactoryForTest(() => fakeAcpProcess(async () => {
        await writeFile(lockfile, "mutated\n")
        await writeFile(artifact, "done\n")
      }))

      const result = await acpAgentAction(context(root))

      expect(result.status).toBe("success")
      expect(await readFile(lockfile, "utf8")).toBe("locked\n")
      expect(await readFile(artifact, "utf8")).toBe("done\n")
      expect(calls).toEqual([
        { command: "git", args: ["checkout", "--", ".opencode/package-lock.json"], workDir: root },
        { command: "git", args: ["checkout", "--", ".opencode/bun.lock"], workDir: root },
        { command: "git", args: ["checkout", "--", ".opencode/node_modules/.package-lock.json"], workDir: root },
      ])
      expect(missingToolFiles).toEqual([".opencode/bun.lock", ".opencode/node_modules/.package-lock.json"])
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
