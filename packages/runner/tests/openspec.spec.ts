import { mkdtemp, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { describe, expect, it, vi } from "vitest"
import { openspecTasksAction } from "../src/actions/openspec.js"
import type { ActionContext } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"

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

    const addTasks = vi.fn()
    const result = await openspecTasksAction(context(workDir, { path: tasksPath }, addTasks))
    const output = JSON.parse(result.output ?? "{}")
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = JSON.parse(loadedTasks[0].with ?? "{}")

    expect(result.status).toBe("success")
    expect(output.loaded).toBe(1)
    expect(addTasks).toHaveBeenCalledWith("workflow-1", expect.any(Array))
    expect(loadedTasks[0].uses).toBe("mohist/acp-agent")
    expect(loadedWith.prompt).toContain("Implement this OpenSpec task: Implement workflow recovery")
    expect(loadedWith.prompt).toContain("runner can claim recovered work")
  })
})

function context(workDir: string, withInput: Record<string, unknown>, addTasks: ServerConnection["addTasks"]): ActionContext {
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
    serverConnection: { addTasks } as ServerConnection,
  }
}
