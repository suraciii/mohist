import { writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, describe, expect, it, vi } from "vitest"
import { openspecTasksAction, setOpenSpecGitRunnerForTest } from "../src/actions/openspec.js"
import type { ActionTestContext as ActionContext } from "./support/action-test-context.js"
import { resolvePrompt, setPromptLoaderRegistryForTest, defaultPromptLoaderRegistry } from "../src/core/prompt.js"
import { renderTemplate } from "../src/core/template.js"
import "../src/core/prompt-registry.js"
import { OPENSPEC_TASK_PROMPT_LOADER_NAME } from "../src/actions/openspec-task-prompt.js"
import { createTestTempDir } from "./support/temp-dir.js"
import { callAction } from "./support/call-action.js"

describe("mohist/openspec-tasks", () => {
  it("OpenSpecTaskWithoutExplicitPrompt_LoadsExecutableAcpTaskWithPromptLoaderSpec", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
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

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const output = result.output as Record<string, unknown>
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(output.loaded).toBe(1)
    expect((result as any).effects?.addTasks).toBeTruthy()
    expect(loadedTasks[0].uses).toBe("mohist/opencode")
    expect(loadedWith.prompt).toEqual({
      uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
      with: { file: tasksPath, items: "tasks", taskId: "T-001" },
    })
  })

  it("OpenSpecTaskWithoutExplicitPrompt_PromptLoaderSpecResolvesThroughRegisteredLoader", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
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

    const result = await callAction(openspecTasksAction, context(workDir, {
      path: tasksPath,
      task: {
        with: {
          prompt: {
            uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
            with: { file: tasksPath, items: "tasks", base: "${{ prompts.build }}" },
          },
        },
      },
    }, { prompts: { build: "<base>build instructions</base>" } }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    setPromptLoaderRegistryForTest(null)
    const renderedWith = renderTemplate(loadedWith, { prompts: { build: "<base>build instructions</base>" } }) as Record<string, unknown>
    const resolved = await resolvePrompt(renderedWith.prompt as never, {
      with: {},
      workDir,
      workId: "load-build",
    })
    expect(resolved).toContain("Implement workflow recovery")
    expect(resolved).toContain("Requeue runnable workflows on server startup.")
    expect(resolved).toContain("runner can claim recovered work")
    expect(resolved).toContain("<base_instructions><base>build instructions</base></base_instructions>")
  })

  it("OpenSpecTaskWithOptionsTemplate_LoadsTaskWithTemplatePreservedForLateExpansion", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, {
      path: tasksPath,
      task: { with: { options: "${{ vars.agent }}" } },
    }, {
      vars: { agent: { type: "opencode", model: "openai/gpt-5.4" } },
    }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.options).toBe("${{ vars.agent }}")
    expect(loadedWith.prompt).toEqual({
      uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
      with: { file: tasksPath, items: "tasks", taskId: "T-001" },
    })
  })

  it("OpenSpecTaskWithoutOptionsTemplate_LoadsTaskWithoutOptions", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }, {
      vars: { agent: { type: "opencode", model: "openai/gpt-5.4" } },
    }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.options).toBeUndefined()
  })

  it("OpenSpecTaskLoader_DoesNotPolluteWithWithDocumentationFields", async () => {
    // The loader used to copy description/notes/output/acceptanceCriteria/dependsOn/
    // priority/mode/type/requireFiles/requireMarkers into `with`. Those fields are
    // loader-internal (used only to build the prompt) and they polluted the
    // action's input — descriptions containing literal `${{ ... }}` would be
    // incorrectly template-rendered by the runner. The fix keeps them out of
    // `with` entirely; task JSON content remains opaque and is read lazily by
    // the registered prompt loader at prompt-resolution time.
    const workDir = await createTestTempDir("mohist-openspec-")
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

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
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
    expect(loadedWith.prompt).toEqual({
      uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
      with: { file: tasksPath, items: "tasks", taskId: "T-001" },
    })
    const promptAsText = JSON.stringify(loadedWith)
    expect(promptAsText).not.toContain("${{ prompts.xxx }}")
    expect(promptAsText).not.toContain("Add skill asset manifest support")
    expect(promptAsText).not.toContain("all 12 .prompt files start with ---")
    expect(promptAsText).not.toContain("12 updated .prompt files")
  })

  it("OpenSpecTaskWithStringPromptOverride_PreservesCallerPrompt", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
          description: "Requeue runnable workflows on server startup.",
          with: { prompt: "literal caller prompt" },
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.prompt).toBe("literal caller prompt")
  })

  it("OpenSpecTaskWithObjectPromptOverride_PreservesCallerPrompt", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
          with: {
            prompt: {
              artifact: { task: "caller structured prompt" },
            },
          },
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.prompt).toEqual({
      artifact: { task: "caller structured prompt" },
    })
  })

  it("OpenSpecTaskWithLoaderPromptOverride_PreservesCallerLoaderSpec", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
          with: {
            prompt: {
              uses: "custom/caller-loader",
              with: { file: "other.json", index: 2 },
            },
          },
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.prompt).toEqual({
      uses: "custom/caller-loader",
      with: { file: "other.json", index: 2 },
    })
  })

  it("OpenSpecTaskWithDefaultWithPromptOverride_PreservesCallerPrompt", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, {
      path: tasksPath,
      task: { with: { prompt: "default-with caller prompt" } },
    }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.prompt).toBe("default-with caller prompt")
  })

  it("OpenSpecTaskWithBuildPromptVariable_EmbedsBaseInLoaderSpec", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }, {
      prompts: { build: "<build>build prompt</build>" },
    }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.prompt).toEqual({
      uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
      with: { file: tasksPath, items: "tasks", taskId: "T-001" },
    })
  })

  it("OpenSpecTaskWithoutBuildPromptVariable_OmitsBaseInLoaderSpec", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.prompt).toEqual({
      uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
      with: { file: tasksPath, items: "tasks", taskId: "T-001" },
    })
  })

  it("OpenSpecTaskWithCustomItemsPath_PropagatesItemsInLoaderSpec", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, {
      path: tasksPath,
      items: "items.nested",
    }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.prompt).toEqual({
      uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
      with: { file: tasksPath, items: "items.nested", taskId: "T-001" },
    })
  })

  it("OpenSpecTaskWithMultipleTasks_InjectsPerTaskSelectors", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "T-001", title: "First" },
        { id: "T-002", title: "Second" },
        { id: "T-003", title: "Third" },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWithList = loadedTasks.map((task: { with?: unknown }) => (task.with ?? {}) as Record<string, unknown>)

    expect(result.error).toBeUndefined()
    expect(loadedTasks).toHaveLength(3)
    expect(loadedWithList.map((with_) => with_.prompt)).toEqual([
      { uses: OPENSPEC_TASK_PROMPT_LOADER_NAME, with: { file: tasksPath, items: "tasks", taskId: "T-001" } },
      { uses: OPENSPEC_TASK_PROMPT_LOADER_NAME, with: { file: tasksPath, items: "tasks", taskId: "T-002" } },
      { uses: OPENSPEC_TASK_PROMPT_LOADER_NAME, with: { file: tasksPath, items: "tasks", taskId: "T-003" } },
    ])
  })

  it("OpenSpecTaskWithDefaultRegistry_OpenSpecTaskPromptLoaderIsRegistered", () => {
    setPromptLoaderRegistryForTest(null)
    expect(defaultPromptLoaderRegistry().has(OPENSPEC_TASK_PROMPT_LOADER_NAME)).toBe(true)
  })

  it("OpenSpecTaskWithDefaultLoaderPromptSpec_PreservesCallersPromptAndInjectsPerTaskTaskId", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "T-001", title: "First" },
        { id: "T-002", title: "Second" },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, {
      path: tasksPath,
      task: {
        uses: "mohist/opencode",
        with: {
          options: "${{ vars.agent }}",
          prompt: {
            uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
            with: {
              file: tasksPath,
              items: "tasks",
              base: "${{ prompts.build }}",
            },
          },
        },
      },
    }, {
      prompts: { build: "<artifact id=\"build-task\">base</artifact>" },
    }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWithList = loadedTasks.map((task: { with?: unknown }) => (task.with ?? {}) as Record<string, unknown>)

    expect(loadedTasks).toHaveLength(2)
    for (const loadedWith of loadedWithList) {
      expect(loadedWith.options).toBe("${{ vars.agent }}")
      expect(loadedWith.prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
      expect(loadedWith.prompt.with.file).toBe(tasksPath)
      expect(loadedWith.prompt.with.items).toBe("tasks")
      expect(loadedWith.prompt.with.base).toBe("${{ prompts.build }}")
    }
    expect(loadedWithList[0].prompt.with.taskId).toBe("T-001")
    expect(loadedWithList[1].prompt.with.taskId).toBe("T-002")
  })

  it("OpenSpecTaskWithDefaultLoaderPromptSpec_AndTaskOverridesWithPrompt_PreservesTaskOverride", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "First",
          with: { prompt: "literal override" },
        },
        { id: "T-002", title: "Second" },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, {
      path: tasksPath,
      task: {
        uses: "mohist/opencode",
        with: {
          options: "${{ vars.agent }}",
          prompt: {
            uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
            with: {
              file: tasksPath,
              items: "tasks",
              base: "${{ prompts.build }}",
            },
          },
        },
      },
    }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWithList = loadedTasks.map((task: { with?: unknown }) => (task.with ?? {}) as Record<string, unknown>)

    expect(loadedWithList[0].prompt).toBe("literal override")
    expect(loadedWithList[1].prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
    expect(loadedWithList[1].prompt.with.taskId).toBe("T-002")
    expect(loadedWithList[1].prompt.with.base).toBe("${{ prompts.build }}")
  })

  it("OpenSpecTaskWithoutProfileUses_DefaultsToMohistOpencode", async () => {
    // T-004 acceptance: when the profile (and the task itself) does
    // not declare a `uses`, the loader MUST default to `mohist/opencode`
    // (the canonical Action for the new contract). The previous
    // default was `mohist/opencode`.
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "T-001", title: "Default uses is opencode" },
        { id: "T-002", title: "Second task also gets opencode" },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []

    expect(result.error).toBeUndefined()
    expect(loadedTasks).toHaveLength(2)
    expect(loadedTasks[0].uses).toBe("mohist/opencode")
    expect(loadedTasks[1].uses).toBe("mohist/opencode")
  })

  it("OpenSpecTaskWithExplicitUses_PreservesCallerChoice", async () => {
    // The default kicks in only when both the profile and the task omit
    // `uses`. A caller may still pin a specific Action (e.g. for a
    // custom recovery task); that choice is preserved.
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "T-001", title: "Custom Action override", uses: "mohist/custom-action" },
        { id: "T-002", title: "Default uses is opencode" },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []

    expect(loadedTasks[0].uses).toBe("mohist/custom-action")
    expect(loadedTasks[1].uses).toBe("mohist/opencode")
  })

  it("OpenSpecTaskWithTaskLevelExpect_PropagatesExpectIntoAddTaskInput", async () => {
    // T-004 acceptance: `mergeTaskWith` propagates `expect` from the
    // task template into the generated AddTaskInput. The executor's
    // completion evaluator owns the contract; the loader MUST NOT
    // swallow `expect`. A missing `expect` becomes `null`.
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Task with completion contract",
          expect: {
            files: [{ path: "review.md" }],
            markers: [{
              path: "review.md",
              oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
              failIf: "<promise>FAIL</promise>",
            }],
          },
        },
        { id: "T-002", title: "Task without expect" },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []

    expect(loadedTasks[0].expect).toEqual({
      files: [{ path: "review.md" }],
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        failIf: "<promise>FAIL</promise>",
      }],
    })
    expect(loadedTasks[1].expect).toBeNull()
  })

  it("OpenSpecTaskWithMarkerOnOutputPath_PropagatesExpectAsIs", async () => {
    // The completion evaluator supports `path: "_output"` against the
    // turn's final assistant text. The loader propagates the marker
    // verbatim; it does not interpret marker structure.
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Output-marker task",
          expect: {
            markers: [{
              path: "_output",
              oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
              failIf: "<promise>FAIL</promise>",
            }],
          },
        },
      ],
    }))

    const result = await callAction(openspecTasksAction, context(workDir, { path: tasksPath }))
    const loadedTasks = (result as any).effects?.addTasks ?? []

    expect(loadedTasks[0].expect).toEqual({
      markers: [{
        path: "_output",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        failIf: "<promise>FAIL</promise>",
      }],
    })
  })

  it("OpenSpecTaskWithExecutorStyleSplitContext_UsesWithAsPassed", async () => {
    const workDir = await createTestTempDir("mohist-openspec-")
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Implement workflow recovery",
        },
      ],
    }))

    const resolvedAgent = { type: "opencode", model: "model-a" }

    const result = await callAction(openspecTasksAction, context(workDir, {
      path: tasksPath,
      task: { with: { options: resolvedAgent } },
    }))
    const loadedTasks = (result as any).effects?.addTasks ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.error).toBeUndefined()
    expect(loadedWith.options).toEqual(resolvedAgent)
  })
})

afterEach(() => {
  setOpenSpecGitRunnerForTest(null)
  setPromptLoaderRegistryForTest(null)
})

function context(workDir: string, withInput: Record<string, unknown>, _serverConnectionDeps?: Record<string, unknown>): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "load-build",
    workType: "load",
    stage: "build",
    title: "Load build tasks",
    uses: "mohist/openspec-tasks",
    with: withInput as never,
    variables: {} as never,
    workDir,
    signal: new AbortController().signal,
    writeVars: vi.fn(),
  }
}
