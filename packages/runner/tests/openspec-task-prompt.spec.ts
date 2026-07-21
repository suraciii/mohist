import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import {
  OPENSPEC_TASK_PROMPT_LOADER_NAME,
  openspecTaskPromptLoader,
} from "../src/actions/openspec-task-prompt.js"
import {
  defaultPromptLoaderRegistry,
  renderStructuredPrompt,
  resolvePrompt,
  setPromptLoaderRegistryForTest,
  type PromptLoaderContext,
} from "../src/core/prompt.js"
import "../src/core/prompt-registry.js"

let workDir = ""
let tasksPath = ""
let buildBasePrompt = "<artifact id=\"build-task\">base instructions</artifact>"

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-task-prompt-"))
  tasksPath = join(workDir, "tasks.json")
  buildBasePrompt = "<artifact id=\"build-task\">base instructions</artifact>"
})

afterEach(async () => {
  setPromptLoaderRegistryForTest(null)
  if (workDir) await rm(workDir, { recursive: true, force: true })
  workDir = ""
  tasksPath = ""
})

describe("mohist/openspec-task-prompt loader - taskId selection", () => {
  it("SelectTaskByTaskId_ResolvesTaskAndEmbedsBaseInstructions", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "T-001", title: "Add structured prompt renderer", description: "First task" },
        { id: "T-002", title: "Add prompt loader", description: "Second task" },
      ],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-002",
      base: buildBasePrompt,
    }))

    expect(result).toEqual({
      artifact: {
        attrs: { id: "T-002" },
        base_instructions: buildBasePrompt,
        selected_task: {
          attrs: { id: "T-002" },
          title: "Add prompt loader",
          description: "Second task",
        },
      },
    })
  })

  it("SelectTaskByTaskIdField_ResolvesWhenIdFieldMissing", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { taskId: "legacy-1", title: "Legacy task id" },
        { taskId: "legacy-2", title: "Other legacy task id" },
      ],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "legacy-1",
    }))

    expect(result).toMatchObject({
      artifact: {
        attrs: { id: "legacy-1" },
        selected_task: { attrs: { id: "legacy-1" }, title: "Legacy task id" },
      },
    })
  })

  it("SelectTaskByTaskId_IdFieldTakesPrecedenceOverTaskIdFieldOnFirstMatch", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "shared", taskId: "shared-fallback", title: "id first" },
        { id: "other", taskId: "shared", title: "duplicate via taskId" },
      ],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "shared",
    }))

    expect(result).toMatchObject({
      artifact: {
        attrs: { id: "shared" },
        selected_task: { title: "id first" },
      },
    })
  })

  it("SelectTaskByTaskId_OverridesProvidedIndex", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "T-001", title: "First" },
        { id: "T-002", title: "Second" },
        { id: "T-003", title: "Third" },
      ],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-003",
      index: 0,
    }))

    expect(result).toMatchObject({
      artifact: {
        attrs: { id: "T-003" },
        selected_task: { title: "Third" },
      },
    })
  })
})

describe("mohist/openspec-task-prompt loader - index selection", () => {
  it("SelectTaskByIndex_ResolvesZeroBasedPosition", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "T-001", title: "First" },
        { id: "T-002", title: "Second" },
        { id: "T-003", title: "Third" },
      ],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      index: 1,
    }))

    expect(result).toEqual({
      artifact: {
        attrs: { id: "T-002" },
        selected_task: {
          attrs: { id: "T-002" },
          title: "Second",
        },
      },
    })
  })

  it("SelectTaskByIndex_WhenTaskHasNoId_EmitsIndexAttribute", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { title: "Anonymous task 1" },
        { title: "Anonymous task 2" },
      ],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      index: 1,
    }))

    expect(result).toEqual({
      artifact: {
        attrs: { index: 1 },
        selected_task: {
          attrs: { index: 1 },
          title: "Anonymous task 2",
        },
      },
    })
  })
})

describe("mohist/openspec-task-prompt loader - clear errors", () => {
  it("MissingFile_FailsWithDescriptiveError", async () => {
    await expect(openspecTaskPromptLoader(loaderContext({
      file: "missing-tasks.json",
      taskId: "T-001",
    }))).rejects.toThrow(/could not find task file: .*missing-tasks\.json/)
  })

  it("MissingItemsPath_FailsWithDescriptiveError", async () => {
    await writeFile(tasksPath, JSON.stringify({
      other: [{ id: "T-001", title: "x" }],
    }))

    await expect(openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-001",
      items: "items",
    }))).rejects.toThrow(/could not find task array at path 'items'/)
  })

  it("ItemsPathResolvesToNonArray_FailsWithDescriptiveError", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: { id: "T-001", title: "x" },
    }))

    await expect(openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-001",
    }))).rejects.toThrow(/did not resolve to an array/)
  })

  it("MissingTaskIdAndIndex_FailsWithSelectorRequiredError", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [{ id: "T-001", title: "x" }],
    }))

    await expect(openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
    }))).rejects.toThrow(/requires either 'taskId' or 'index'/)
  })

  it("MissingTaskIdMatch_FailsWithSelectedTaskError", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [{ id: "T-001", title: "x" }],
    }))

    await expect(openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-999",
    }))).rejects.toThrow(/could not find task with id 'T-999'/)
  })

  it("IndexOutOfRange_FailsWithDescriptiveError", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [{ id: "T-001", title: "x" }],
    }))

    await expect(openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      index: 5,
    }))).rejects.toThrow(/index 5 is out of range/)
  })

  it("MissingFilePath_FailsWithDescriptiveError", async () => {
    await expect(openspecTaskPromptLoader(loaderContext({
      taskId: "T-001",
    }))).rejects.toThrow(/requires 'file'/)
  })

  it("MalformedJson_FailsWithParseError", async () => {
    await writeFile(tasksPath, "{ not valid json")

    await expect(openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-001",
    }))).rejects.toThrow(/could not parse task file/)
  })
})

describe("mohist/openspec-task-prompt loader - opaque JSON content", () => {
  it("TaskDescriptionContainingLiteralTemplateSyntax_IsPreservedAsData", async () => {
    const description = "Prepend a YAML block. bodies must remain byte-identical so the runner's ${{ prompts.xxx }} resolution is unaffected."
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Add skill asset manifest support",
          description,
          acceptanceCriteria: ["all 12 .prompt files start with ---"],
          dependsOn: [],
          notes: "use the existing CLI build identity source where available",
          output: "12 updated .prompt files",
        },
      ],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-001",
    }))

    expect(result).toMatchObject({
      artifact: {
        selected_task: {
          description,
          acceptanceCriteria: ["all 12 .prompt files start with ---"],
          dependsOn: [],
          notes: "use the existing CLI build identity source where available",
          output: "12 updated .prompt files",
        },
      },
    })
    const serialized = JSON.stringify(result)
    expect(serialized).toContain("${{ prompts.xxx }}")
  })
})

describe("mohist/openspec-task-prompt loader - configuration", () => {
  it("CustomItemsPath_LocatesTaskArrayAtNestedPath", async () => {
    await writeFile(tasksPath, JSON.stringify({
      openspec: { items: [{ id: "T-007", title: "Nested task" }] },
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      items: "openspec.items",
      taskId: "T-007",
    }))

    expect(result).toMatchObject({
      artifact: {
        selected_task: { title: "Nested task" },
      },
    })
  })

  it("CustomRootTag_UsesConfiguredOuterTag", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [{ id: "T-001", title: "First" }],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-001",
      root: "contract",
    }))

    expect(Object.keys(result)).toEqual(["contract"])
    expect((result as { contract: unknown }).contract).toBeDefined()
  })

  it("EmptyBase_OmitsBaseInstructionsField", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [{ id: "T-001", title: "First" }],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-001",
    }))

    expect(result).toEqual({
      artifact: {
        attrs: { id: "T-001" },
        selected_task: { attrs: { id: "T-001" }, title: "First" },
      },
    })
  })

  it("BlankBase_OmitsBaseInstructionsField", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [{ id: "T-001", title: "First" }],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "tasks.json",
      taskId: "T-001",
      base: "   ",
    }))

    expect(result).toEqual({
      artifact: {
        attrs: { id: "T-001" },
        selected_task: { attrs: { id: "T-001" }, title: "First" },
      },
    })
  })

  it("RelativeFilePath_ResolvesAgainstWorkDir", async () => {
    const nestedDir = join(workDir, "nested")
    const nested = join(nestedDir, "tasks.json")
    await mkdir(nestedDir, { recursive: true })
    await writeFile(nested, JSON.stringify({
      tasks: [{ id: "T-001", title: "Nested location" }],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: "nested/tasks.json",
      taskId: "T-001",
    }))

    expect(result).toMatchObject({
      artifact: { selected_task: { title: "Nested location" } },
    })
  })

  it("AbsoluteFilePath_UsesAsIs", async () => {
    const absolute = join(workDir, "absolute.json")
    await writeFile(absolute, JSON.stringify({
      tasks: [{ id: "T-001", title: "Absolute path" }],
    }))

    const result = await openspecTaskPromptLoader(loaderContext({
      file: absolute,
      taskId: "T-001",
    }))

    expect(result).toMatchObject({
      artifact: { selected_task: { title: "Absolute path" } },
    })
  })

  it("NonObjectRoot_FailsWithDescriptiveError", async () => {
    const arrayRootPath = join(workDir, "array.json")
    await writeFile(arrayRootPath, JSON.stringify([{ id: "T-001", title: "x" }]))

    await expect(openspecTaskPromptLoader(loaderContext({
      file: "array.json",
      taskId: "T-001",
    }))).rejects.toThrow(/root is not a JSON object/)
  })
})

describe("mohist/openspec-task-prompt loader - integration with default renderer", () => {
  it("LoaderResult_RendersThroughStructuredRendererWhenResolvedViaRegistry", async () => {
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        {
          id: "T-001",
          title: "Add structured prompt renderer",
          description: "first line\nsecond line",
          acceptanceCriteria: ["first criterion", "second criterion"],
          output: "packages/runner/src/core/prompt.ts",
        },
      ],
    }))

    setPromptLoaderRegistryForTest(null)
    const registry = defaultPromptLoaderRegistry()
    expect(registry.has(OPENSPEC_TASK_PROMPT_LOADER_NAME)).toBe(true)

    const text = await resolvePrompt({
      uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
      with: {
        file: "tasks.json",
        taskId: "T-001",
        base: buildBasePrompt,
      },
    }, loaderContext({}))

    const direct = renderStructuredPrompt({
      artifact: {
        attrs: { id: "T-001" },
        base_instructions: buildBasePrompt,
        selected_task: {
          attrs: { id: "T-001" },
          title: "Add structured prompt renderer",
          description: "first line\nsecond line",
          acceptanceCriteria: ["first criterion", "second criterion"],
          output: "packages/runner/src/core/prompt.ts",
        },
      },
    })

    expect(text).toBe(direct)
  })
})

function loaderContext(with_: Record<string, unknown>): PromptLoaderContext {
  return {
    with: with_ as PromptLoaderContext["with"],
    workDir,
    workId: "work-1",
  }
}
