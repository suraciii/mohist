import { mkdtemp, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { describe, expect, it } from "vitest"
import { openspecTasksAction } from "../src/actions/openspec.js"
import type { ActionContext } from "../src/core/types.js"

describe("mohist/openspec-tasks", () => {
  it("OpenSpecTaskWithoutExplicitPrompt_LoadsExecutableAcpTaskWithPrompt", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
          description: "Requeue runnable workflows on server startup.",
          acceptanceCriteria: ["runner can claim recovered work"],
          output: "packages/server/src/Mohist.Server/Workflow/Recovery",
        },
      ],
    }))

    const result = await openspecTasksAction(context(workDir, { path: tasksPath }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("loaded")
    expect(output.tasks[0].uses).toBe("mohist/acp-agent")
    expect(output.tasks[0].with.prompt).toContain("Implement this OpenSpec task: Implement workflow recovery")
    expect(output.tasks[0].with.prompt).toContain("runner can claim recovered work")
  })
})

function context(workDir: string, withInput: Record<string, unknown>): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "load-build",
    workType: "load",
    stage: "build",
    title: "Load build tasks",
    uses: "mohist/openspec-tasks",
    with: withInput as never,
    variables: {},
    workDir,
    signal: new AbortController().signal,
  }
}
