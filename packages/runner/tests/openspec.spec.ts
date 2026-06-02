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

  it("OpenSpecTaskWithAgentTemplate_LoadsTaskWithTemplatePreservedForLateExpansion", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
        },
      ],
    }))

    const addTasks = vi.fn()
    const result = await openspecTasksAction(context(workDir, {
      path: tasksPath,
      task: { with: { agent: "${{ vars.agent }}" } },
    }, addTasks, {
      vars: { agent: { type: "opencode", model: "openai/gpt-5.4" } },
    }))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = JSON.parse(loadedTasks[0].with ?? "{}")

    expect(result.status).toBe("success")
    expect(loadedWith.agent).toBe("${{ vars.agent }}")
  })

  it("OpenSpecTaskWithoutAgentTemplate_LoadsTaskWithoutAgent", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
        },
      ],
    }))

    const addTasks = vi.fn()
    const result = await openspecTasksAction(context(workDir, { path: tasksPath }, addTasks, {
      vars: { agent: { type: "opencode", model: "openai/gpt-5.4" } },
    }))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = JSON.parse(loadedTasks[0].with ?? "{}")

    expect(result.status).toBe("success")
    expect(loadedWith.agent).toBeUndefined()
  })

  it("OpenSpecTaskLoader_DoesNotPolluteWithWithDocumentationFields", async () => {
    // The loader used to copy description/notes/output/acceptanceCriteria/dependsOn/
    // priority/mode/type/requireFiles/requireMarkers into `with`. Those fields are
    // loader-internal (used only to build the prompt) and they polluted the
    // action's input — descriptions containing literal `${{ ... }}` would be
    // incorrectly template-rendered by the runner. The fix keeps them out of
    // `with` entirely; they live only inside the constructed prompt.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Add skill asset manifest support",
          description: "Prepend a YAML block. bodies must remain byte-identical so the runner's ${{ prompts.xxx }} resolution is unaffected.",
          acceptanceCriteria: ["all 12 .prompt files start with ---"],
          dependsOn: [],
          notes: "use the existing CLI build identity source where available",
          output: "12 updated .prompt files",
          priority: 1,
          mode: "AFK",
          type: "WRITE",
          requireFiles: ["/tmp/a.prompt"],
          requireMarkers: ["PASS"],
        },
      ],
    }))

    const addTasks = vi.fn()
    const result = await openspecTasksAction(context(workDir, { path: tasksPath }, addTasks))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = JSON.parse(loadedTasks[0].with ?? "{}")

    expect(result.status).toBe("success")
    // None of these loader-internal fields should appear on the wire:
    expect(loadedWith.description).toBeUndefined()
    expect(loadedWith.notes).toBeUndefined()
    expect(loadedWith.output).toBeUndefined()
    expect(loadedWith.acceptanceCriteria).toBeUndefined()
    expect(loadedWith.dependsOn).toBeUndefined()
    expect(loadedWith.priority).toBeUndefined()
    expect(loadedWith.mode).toBeUndefined()
    expect(loadedWith.type).toBeUndefined()
    expect(loadedWith.requireFiles).toBeUndefined()
    expect(loadedWith.requireMarkers).toBeUndefined()
    // The literal `${{ prompts.xxx }}` text is still preserved — inside the prompt.
    expect(loadedWith.prompt).toContain("Add skill asset manifest support")
    expect(loadedWith.prompt).toContain("${{ prompts.xxx }}")
    expect(loadedWith.prompt).toContain("all 12 .prompt files start with ---")
    expect(loadedWith.prompt).toContain("12 updated .prompt files")
  })
})

function context(workDir: string, withInput: Record<string, unknown>, addTasks: ServerConnection["addTasks"], variables: Record<string, unknown> = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "load-build",
    workType: "load",
    stage: "build",
    title: "Load build tasks",
    uses: "mohist/openspec-tasks",
    with: withInput as never,
    variables: variables as never,
    workDir,
    signal: new AbortController().signal,
    serverConnection: { addTasks } as ServerConnection,
  }
}
