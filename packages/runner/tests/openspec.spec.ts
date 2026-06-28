import { mkdir, mkdtemp, rename, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, describe, expect, it, vi } from "vitest"
import { archiveChangeAction, openspecArtifactsAction, openspecSyncAction, openspecTasksAction, setArchiveRenameForTest, setOpenSpecGitRunnerForTest } from "../src/actions/openspec.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { resolvePrompt, setPromptLoaderRegistryForTest, defaultPromptLoaderRegistry } from "../src/core/prompt.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import "../src/core/prompt-registry.js"
import { OPENSPEC_TASK_PROMPT_LOADER_NAME } from "../src/actions/openspec-task-prompt.js"

describe("mohist/openspec-tasks", () => {
  it("OpenSpecTaskWithoutExplicitPrompt_LoadsExecutableAcpTaskWithPromptLoaderSpec", async () => {
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
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(output.loaded).toBe(1)
    expect(addTasks).toHaveBeenCalledWith("workflow-1", expect.any(Array))
    expect(loadedTasks[0].uses).toBe("mohist/acp-agent")
    expect(loadedWith.prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
    expect(loadedWith.prompt.with).toEqual({
      file: tasksPath,
      items: "tasks",
      taskId: "T-001",
    })
  })

  it("OpenSpecTaskWithoutExplicitPrompt_PromptLoaderSpecResolvesThroughRegisteredLoader", async () => {
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
    await openspecTasksAction(context(workDir, { path: tasksPath }, addTasks, {
      prompts: { build: "<base>build instructions</base>" },
    }))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    setPromptLoaderRegistryForTest(null)
    const resolved = await resolvePrompt(loadedWith.prompt, {
      with: {},
      variables: {},
      workDir,
      workId: "load-build",
    })
    expect(resolved).toContain("Implement workflow recovery")
    expect(resolved).toContain("Requeue runnable workflows on server startup.")
    expect(resolved).toContain("runner can claim recovered work")
    expect(resolved).toContain("<base>build instructions</base>")
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
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(loadedWith.agent).toBe("${{ vars.agent }}")
    // Default prompt is still injected as the loader spec.
    expect(loadedWith.prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
    expect(loadedWith.prompt.with.taskId).toBe("T-001")
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
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(loadedWith.agent).toBeUndefined()
  })

  it("OpenSpecTaskLoader_DoesNotPolluteWithWithDocumentationFields", async () => {
    // The loader used to copy description/notes/output/acceptanceCriteria/dependsOn/
    // priority/mode/type/requireFiles/requireMarkers into `with`. Those fields are
    // loader-internal (used only to build the prompt) and they polluted the
    // action's input — descriptions containing literal `${{ ... }}` would be
    // incorrectly template-rendered by the runner. The fix keeps them out of
    // `with` entirely; task JSON content remains opaque and is read lazily by
    // the registered prompt loader at prompt-resolution time.
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
    const loadedWith = loadedTasks[0]?.with ?? {}

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
    // The default prompt is now a loader spec, not a literal string, so the
    // task description (including literal `${{ prompts.xxx }}` text) is
    // preserved inside tasks.json and is never embedded into generated `with`
    // values.
    expect(loadedWith.prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
    expect(loadedWith.prompt.with).toEqual({
      file: tasksPath,
      items: "tasks",
      taskId: "T-001",
    })
    const promptAsText = JSON.stringify(loadedWith)
    expect(promptAsText).not.toContain("${{ prompts.xxx }}")
    expect(promptAsText).not.toContain("Add skill asset manifest support")
    expect(promptAsText).not.toContain("all 12 .prompt files start with ---")
    expect(promptAsText).not.toContain("12 updated .prompt files")
  })

  it("OpenSpecTaskWithStringPromptOverride_PreservesCallerPrompt", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
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

    const addTasks = vi.fn()
    const result = await openspecTasksAction(context(workDir, { path: tasksPath }, addTasks))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(loadedWith.prompt).toBe("literal caller prompt")
  })

  it("OpenSpecTaskWithObjectPromptOverride_PreservesCallerPrompt", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
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

    const addTasks = vi.fn()
    const result = await openspecTasksAction(context(workDir, { path: tasksPath }, addTasks))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(loadedWith.prompt).toEqual({
      artifact: { task: "caller structured prompt" },
    })
  })

  it("OpenSpecTaskWithLoaderPromptOverride_PreservesCallerLoaderSpec", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
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

    const addTasks = vi.fn()
    const result = await openspecTasksAction(context(workDir, { path: tasksPath }, addTasks))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(loadedWith.prompt).toEqual({
      uses: "custom/caller-loader",
      with: { file: "other.json", index: 2 },
    })
  })

  it("OpenSpecTaskWithDefaultWithPromptOverride_PreservesCallerPrompt", async () => {
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
      task: { with: { prompt: "default-with caller prompt" } },
    }, addTasks))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(loadedWith.prompt).toBe("default-with caller prompt")
  })

  it("OpenSpecTaskWithBuildPromptVariable_EmbedsBaseInLoaderSpec", async () => {
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
      prompts: { build: "<build>build prompt</build>" },
    }))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(loadedWith.prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
    expect(loadedWith.prompt.with.base).toBe("<build>build prompt</build>")
    expect(loadedWith.prompt.with.taskId).toBe("T-001")
  })

  it("OpenSpecTaskWithoutBuildPromptVariable_OmitsBaseInLoaderSpec", async () => {
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
    const result = await openspecTasksAction(context(workDir, { path: tasksPath }, addTasks))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(loadedWith.prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
    expect(loadedWith.prompt.with.base).toBeUndefined()
    expect(loadedWith.prompt.with).toEqual({
      file: tasksPath,
      items: "tasks",
      taskId: "T-001",
    })
  })

  it("OpenSpecTaskWithCustomItemsPath_PropagatesItemsInLoaderSpec", async () => {
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
      items: "items.nested",
    }, addTasks))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWith = loadedTasks[0]?.with ?? {}

    expect(result.status).toBe("success")
    expect(loadedWith.prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
    expect(loadedWith.prompt.with.items).toBe("items.nested")
  })

  it("OpenSpecTaskWithMultipleTasks_InjectsPerTaskSelectors", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "T-001", title: "First" },
        { id: "T-002", title: "Second" },
        { id: "T-003", title: "Third" },
      ],
    }))

    const addTasks = vi.fn()
    const result = await openspecTasksAction(context(workDir, { path: tasksPath }, addTasks))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWithList = loadedTasks.map((task: { with?: unknown }) => (task.with ?? {}) as Record<string, unknown>)

    expect(result.status).toBe("success")
    expect(loadedTasks).toHaveLength(3)
    expect(loadedWithList[0].prompt.with.taskId).toBe("T-001")
    expect(loadedWithList[1].prompt.with.taskId).toBe("T-002")
    expect(loadedWithList[2].prompt.with.taskId).toBe("T-003")
    for (const with_ of loadedWithList) {
      expect(with_.prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
      expect(with_.prompt.with.file).toBe(tasksPath)
    }
  })

  it("OpenSpecTaskWithDefaultRegistry_OpenSpecTaskPromptLoaderIsRegistered", () => {
    setPromptLoaderRegistryForTest(null)
    expect(defaultPromptLoaderRegistry().has(OPENSPEC_TASK_PROMPT_LOADER_NAME)).toBe(true)
  })

  it("OpenSpecTaskWithDefaultLoaderPromptSpec_PreservesCallersPromptAndInjectsPerTaskTaskId", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
    const tasksPath = join(workDir, "tasks.json")
    await writeFile(tasksPath, JSON.stringify({
      tasks: [
        { id: "T-001", title: "First" },
        { id: "T-002", title: "Second" },
      ],
    }))

    const addTasks = vi.fn()
    await openspecTasksAction(context(workDir, {
      path: tasksPath,
      task: {
        uses: "mohist/acp-agent",
        with: {
          agent: "${{ vars.agent }}",
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
    }, addTasks, {
      prompts: { build: "<artifact id=\"build-task\">base</artifact>" },
    }))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWithList = loadedTasks.map((task: { with?: unknown }) => (task.with ?? {}) as Record<string, unknown>)

    expect(loadedTasks).toHaveLength(2)
    for (const loadedWith of loadedWithList) {
      expect(loadedWith.agent).toBe("${{ vars.agent }}")
      expect(loadedWith.prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
      expect(loadedWith.prompt.with.file).toBe(tasksPath)
      expect(loadedWith.prompt.with.items).toBe("tasks")
      expect(loadedWith.prompt.with.base).toBe("${{ prompts.build }}")
    }
    expect(loadedWithList[0].prompt.with.taskId).toBe("T-001")
    expect(loadedWithList[1].prompt.with.taskId).toBe("T-002")
  })

  it("OpenSpecTaskWithDefaultLoaderPromptSpec_AndTaskOverridesWithPrompt_PreservesTaskOverride", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-"))
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

    const addTasks = vi.fn()
    await openspecTasksAction(context(workDir, {
      path: tasksPath,
      task: {
        uses: "mohist/acp-agent",
        with: {
          agent: "${{ vars.agent }}",
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
    }, addTasks))
    const loadedTasks = addTasks.mock.calls[0]?.[1] ?? []
    const loadedWithList = loadedTasks.map((task: { with?: unknown }) => (task.with ?? {}) as Record<string, unknown>)

    expect(loadedWithList[0].prompt).toBe("literal override")
    expect(loadedWithList[1].prompt.uses).toBe(OPENSPEC_TASK_PROMPT_LOADER_NAME)
    expect(loadedWithList[1].prompt.with.taskId).toBe("T-002")
    expect(loadedWithList[1].prompt.with.base).toBe("${{ prompts.build }}")
  })
})

afterEach(() => {
  setOpenSpecGitRunnerForTest(null)
  setArchiveRenameForTest(null)
})

describe("mohist/openspec-sync", () => {
  it("OpenSpecSyncAfterCopy_StagesAndCommitsSpecsDirectoryWithExpectedMessage", async () => {
    // After copyDirectory has populated the worktree's specs/
    // directory, the action must run `git add specs/`, observe
    // staged changes, and commit them with the expected message.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-sync-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-112")
    const sourceSpecs = join(changeDir, "specs", "workflow-definition")
    await mkdir(sourceSpecs, { recursive: true })
    await writeFile(join(sourceSpecs, "spec.md"), "## MODIFIED\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === "add specs/") return gitOk("")
      if (key === "diff --cached --name-only -- specs/") {
        return gitOk("specs/workflow-definition/spec.md\n")
      }
      if (key === "commit -m Sync OpenSpec specs from change delta -- specs/") {
        return gitOk("[main abc1234] Sync OpenSpec specs from change delta\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("abc1234\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await openspecSyncAction(syncContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toMatch(/committed/)
    expect(output.kind).toBe("openspec-sync")
    expect(output.changed).toBe(true)
    expect(output.noChange).toBe(false)
    expect(output.commitMessage).toBe("Sync OpenSpec specs from change delta")
    expect(output.commitSha).toBe("abc1234")
    expect(output.changedFiles).toEqual(["specs/workflow-definition/spec.md"])
    expect(calls.map((args) => args.join(" "))).toEqual([
      "add specs/",
      "diff --cached --name-only -- specs/",
      "commit -m Sync OpenSpec specs from change delta -- specs/",
      "rev-parse HEAD",
    ])
  })

  it("OpenSpecSyncWhenAlreadyUpToDate_ReturnsSuccessWithNoChangeMarker", async () => {
    // If copyDirectory reproduces the worktree's existing specs,
    // `git add specs/` adds nothing and `git diff --cached
    // --name-only -- specs/` returns no files. The action must
    // short-circuit, skip the commit, and return success with a
    // no-change marker so the executor's post-action clean
    // worktree check can pass without burning a commit.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-sync-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-112")
    const sourceSpecs = join(changeDir, "specs")
    await mkdir(sourceSpecs, { recursive: true })
    await writeFile(join(sourceSpecs, "spec.md"), "## MODIFIED\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === "add specs/") return gitOk("")
      if (key === "diff --cached --name-only -- specs/") return gitOk("")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await openspecSyncAction(syncContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toMatch(/no change/i)
    expect(output.changed).toBe(false)
    expect(output.noChange).toBe(true)
    expect(calls.map((args) => args.join(" "))).toEqual([
      "add specs/",
      "diff --cached --name-only -- specs/",
    ])
  })

  it("OpenSpecSyncWhenAddFails_FailsWithStageAddAndStopsBeforeCommit", async () => {
    // A failure in `git add specs/` must surface as a structured
    // failure that names the `add` stage; the action must not
    // attempt the diff or commit.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-sync-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-112")
    const sourceSpecs = join(changeDir, "specs")
    await mkdir(sourceSpecs, { recursive: true })
    await writeFile(join(sourceSpecs, "spec.md"), "## MODIFIED\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === "add specs/") {
        return gitFail("fatal: pathspec 'specs/' did not match any files", 128)
      }
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await openspecSyncAction(syncContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toMatch(/git add specs\/ failed/)
    expect(result.message).toContain("pathspec 'specs/' did not match any files")
    expect(output.stage).toBe("add")
    expect(calls.map((args) => args.join(" "))).toEqual(["add specs/"])
  })

  it("OpenSpecSyncWhenCommitFails_FailsWithStageCommitAndPreservesChangedFiles", async () => {
    // If `git add` and `git diff --cached` succeed but `git commit`
    // fails (e.g. pre-commit hook, repo permission, GPG sign
    // error), the action must report the failure with the
    // `commit` stage and the list of files that were staged.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-sync-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-112")
    const sourceSpecs = join(changeDir, "specs")
    await mkdir(sourceSpecs, { recursive: true })
    await writeFile(join(sourceSpecs, "spec.md"), "## MODIFIED\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === "add specs/") return gitOk("")
      if (key === "diff --cached --name-only -- specs/") {
        return gitOk("specs/spec.md\n")
      }
      if (key === "commit -m Sync OpenSpec specs from change delta -- specs/") {
        return gitFail("fatal: cannot commit without a user identity", 128)
      }
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await openspecSyncAction(syncContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toMatch(/git commit specs\/ failed/)
    expect(result.message).toContain("cannot commit without a user identity")
    expect(output.stage).toBe("commit")
    expect(output.changedFiles).toEqual(["specs/spec.md"])
    expect(calls.map((args) => args.join(" "))).toEqual([
      "add specs/",
      "diff --cached --name-only -- specs/",
      "commit -m Sync OpenSpec specs from change delta -- specs/",
    ])
  })
})

describe("mohist/archive-change", () => {
  afterEach(() => {
    setArchiveRenameForTest(null)
  })

  it("ArchiveChangeAfterMove_StagesAndCommitsArchivedChange", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs", "workflow-definition"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "workflow-definition", "spec.md"), "spec\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`
    const destination = join(workDir, destinationRel)
    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk([
          `${destinationRel}/proposal.md`,
          `${destinationRel}/specs/workflow-definition/spec.md`,
        ].join("\n") + "\n")
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main def5678] Archive OpenSpec change: issue-127\n 3 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("def5678\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toMatch(/archived and committed/)
    expect(output.kind).toBe("archive-change")
    expect(output.destination).toBe(destination)
    expect(output.changed).toBe(true)
    expect(output.noChange).toBe(false)
    expect(output.commitMessage).toBe("Archive OpenSpec change: issue-127")
    expect(output.commitSha).toBe("def5678")
    expect(output.errorCode).toBeNull()
    expect(output.changedFiles).toEqual([
      `${destinationRel}/proposal.md`,
      `${destinationRel}/specs/workflow-definition/spec.md`,
    ])
    expect(calls.map((args) => args.join(" "))).toEqual([
      `add -A ${destinationRel}`,
      `rm -rf --cached --ignore-unmatch ${sourceRel}`,
      `diff --cached --name-only -- ${sourceRel} ${destinationRel}`,
      `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`,
      "rev-parse HEAD",
    ])
  })

  it("ArchiveChangeRetriedAfterRename_SkipsRenameAndResumesFromStage", async () => {
    // Simulates a previous run that completed the rename but crashed before
    // any git call. The retry must observe the existing archive on disk,
    // skip the rename, and complete the staging + commit.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`
    const archivedDir = join(workDir, destinationRel)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main abc1234] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("abc1234\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toMatch(/archived and committed/)
    expect(output.destination).toBe(archivedDir)
    expect(output.changed).toBe(true)
    expect(output.noChange).toBe(false)
    expect(output.commitSha).toBe("abc1234")
    expect(calls.map((args) => args.join(" "))).toEqual([
      `add -A ${destinationRel}`,
      `rm -rf --cached --ignore-unmatch ${sourceRel}`,
      `diff --cached --name-only -- ${sourceRel} ${destinationRel}`,
      `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`,
      "rev-parse HEAD",
    ])
  })

  it("ArchiveChangeRetriedAfterSuccessfulCommit_SkipsCommitAndReturnsNoChange", async () => {
    // Simulates a previous run that completed both the rename and the
    // commit. The retry must observe the empty stage for both source and
    // archive paths and return success without making another commit.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`
    const archivedDir = join(workDir, destinationRel)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) return gitOk("")
      if (key.startsWith("commit ")) return gitFail(`unexpected commit after retry: ${key}`, 1)
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toMatch(/already archived/)
    expect(output.destination).toBe(archivedDir)
    expect(output.changed).toBe(false)
    expect(output.noChange).toBe(true)
    expect(output.errorCode).toBeNull()
    expect(calls.map((args) => args.join(" "))).toEqual([
      `add -A ${destinationRel}`,
      `rm -rf --cached --ignore-unmatch ${sourceRel}`,
      `diff --cached --name-only -- ${sourceRel} ${destinationRel}`,
    ])
  })

  it("ArchiveChangeRetriedAfterStageBeforeCommit_ResumesFromStage", async () => {
    // Simulates a previous run that crashed between `git add`/`git rm` and
    // `git commit`. The retry must re-run the (idempotent) stage and diff,
    // observe non-empty stage, and successfully commit.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`
    const archivedDir = join(workDir, destinationRel)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main 9999999] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("9999999\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.commitSha).toBe("9999999")
    expect(calls.map((args) => args.join(" "))).toContain(
      `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`,
    )
  })

  it("ArchiveChangeWhenPersistedDestinationAndSourceBothExist_FailsWithPartialArchive", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const archiveName = `${datePrefix}-issue-127`
    const archivedDir = join(workDir, "openspec", "changes", "archive", archiveName)
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "source proposal\n")
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "archive proposal\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      "_actions.archiveChange.destination": { [sourceRel]: archiveName },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain(changeDir)
    expect(result.message).toContain(archivedDir)
    expect(output.errorCode).toBe("partial-archive")
    expect(output.source).toBe(changeDir)
    expect(output.archive).toBe(archivedDir)
    expect(calls).toEqual([])
  })

  it("ArchiveChangeWhenSourceMissingAndArchiveMissing_FailsWithMissingSource", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toMatch(/not found/)
    expect(result.message).toContain(changeDir)
    expect(output.errorCode).toBe("missing-source")
    expect(output.source).toBe(changeDir)
    expect(calls).toEqual([])
  })

  it("ArchiveChangeOnCrossDeviceRename_FallsBackToCopyAndStillCommits", async () => {
    // When the source and destination are on different filesystems, the
    // initial `rename` fails with EXDEV. The action must fall back to a
    // recursive copy + delete and continue with the rest of the flow.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`

    setArchiveRenameForTest(async () => {
      const err = new Error("EXDEV: cross-device link not permitted") as NodeJS.ErrnoException
      err.code = "EXDEV"
      throw err
    })

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n${destinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main fed4321] Archive OpenSpec change: issue-127\n 2 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("fed4321\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toMatch(/archived and committed/)
    expect(output.commitSha).toBe("fed4321")
    expect(output.errorCode).toBeNull()
    expect(calls.map((args) => args.join(" "))).toContain(
      `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`,
    )
  })

  it("ArchiveChangeWithUnrelatedStagedChange_DoesNotIncludeUnrelatedPathInArchiveCommit", async () => {
    // The action must scope its stage/diff/commit to source + archive
    // paths so an unrelated staged change under `openspec/` does not get
    // swept into the archive commit.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(changeDir, { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`

    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        // Only the archive path appears in the pathspec-filtered diff;
        // an unrelated staged file is filtered out by the pathspec.
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main 1112223] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("1112223\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.changedFiles).toEqual([`${destinationRel}/proposal.md`])
    expect(output.changedFiles.find((file: string) => file.includes("unrelated"))).toBeUndefined()
  })

  it("ArchiveChangeWhenCommitFails_FailsWithStageCommitAndPreservesChangedFiles", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(changeDir, { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`

    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitFail("fatal: cannot commit without a user identity", 128)
      }
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toMatch(/git commit archive change failed/)
    expect(result.message).toContain("cannot commit without a user identity")
    expect(output.errorCode).toBe("retry-safe")
    expect(output.stage).toBe("commit")
    expect(output.changedFiles).toEqual([`${destinationRel}/proposal.md`])
  })

  it("ArchiveChangePersistsArchiveNameBeforeMove", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`

    const patchRunVars = vi.fn()
    let writeSeenBeforeMove = false
    setArchiveRenameForTest(async (src, dst) => {
      if (patchRunVars.mock.calls.length > 0) writeSeenBeforeMove = true
      await rename(src, dst)
    })

    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n${destinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main def5678] Archive OpenSpec change: issue-127\n 3 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("def5678\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.destination).toBe(join(workDir, destinationRel))
    expect(writeSeenBeforeMove).toBe(true)
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: `${datePrefix}-issue-127` },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeRetryAfterVersionedMove_ReusesExactPersistedDestination", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal v2\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const baseArchiveName = `${datePrefix}-issue-127`
    const versionedArchiveName = `${baseArchiveName}-v2`
    const baseArchiveRel = `openspec/changes/archive/${baseArchiveName}`
    const versionedDestinationRel = `openspec/changes/archive/${versionedArchiveName}`
    const versionedDestination = join(workDir, versionedDestinationRel)
    await mkdir(join(workDir, baseArchiveRel), { recursive: true })
    await writeFile(join(workDir, baseArchiveRel, "proposal.md"), "older archive\n")

    let persistedVars: JsonObject = {}
    const firstPatchRunVars = vi.fn(async (_workflowRunId: string, vars: JsonObject) => {
      persistedVars = { ...persistedVars, ...vars }
    })

    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${versionedDestinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${versionedDestinationRel}`) {
        return gitOk(`${versionedDestinationRel}/proposal.md\n${versionedDestinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${versionedDestinationRel}`) {
        return gitFail("pre-commit hook failed", 1)
      }
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const firstResult = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars: firstPatchRunVars }))
    const firstOutput = JSON.parse(firstResult.output ?? "{}")

    expect(firstResult.status).toBe("failure")
    expect(firstOutput.stage).toBe("commit")
    expect(firstOutput.destination).toBe(versionedDestination)
    expect(firstPatchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: versionedArchiveName },
      expect.any(AbortSignal),
    )

    const retryPatchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${versionedDestinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${versionedDestinationRel}`) {
        return gitOk(`${versionedDestinationRel}/proposal.md\n${versionedDestinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${versionedDestinationRel}`) {
        return gitOk("[main abc1234] Archive OpenSpec change: issue-127\n 2 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("abc1234\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const retryResult = await archiveChangeAction(archiveContext(workDir, changeDir, persistedVars, { patchRunVars: retryPatchRunVars }))
    const retryOutput = JSON.parse(retryResult.output ?? "{}")

    expect(retryResult.status).toBe("success")
    expect(retryOutput.destination).toBe(versionedDestination)
    expect(retryOutput.commitSha).toBe("abc1234")
    expect(retryPatchRunVars).not.toHaveBeenCalled()
  })

  it("ArchiveChangeCrossDayRetry_ReusesPersistedNameAndFindsArchivedDirectory", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const oldPrefix = "2026-06-25-issue-127"
    const archivedDir = join(workDir, "openspec", "changes", "archive", oldPrefix)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${oldPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main abc1234] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("abc1234\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      openspecArchiveName: oldPrefix,
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.destination).toBe(archivedDir)
    expect(patchRunVars).not.toHaveBeenCalled()
  })

  it.each([
    ["openspecArchiveName", "../escaped"],
    ["openspecArchiveName", "../../escaped"],
    ["openspecArchiveName", "nested/name"],
    ["_actions.archiveChange.destination", "../escaped"],
    ["_actions.archiveChange.destination", "../../escaped"],
    ["_actions.archiveChange.destination", "nested/name"],
  ] as const)("ArchiveChangeRejectsUnsafePersistedName_%s_%s", async (keySource, unsafePrefix) => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")

    const calls: string[][] = []
    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const variables = keySource === "openspecArchiveName"
      ? { openspecArchiveName: unsafePrefix }
      : { "_actions.archiveChange.destination": { "openspec/changes/issue-127": unsafePrefix } }

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, variables, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.errorCode).toBe("config-error")
    expect(output.stage).toBe("validate-archive-name")
    expect(output.archivePrefix).toBe(unsafePrefix)
    expect(calls).toEqual([])
    expect(patchRunVars).not.toHaveBeenCalled()
  })

  it("ArchiveChangeRetryWithPersistedNameAndNoMove_ReusesNameAndMovesToPersistedDestination", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const oldPrefix = "2026-06-25-issue-127"
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${oldPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n${destinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main def5678] Archive OpenSpec change: issue-127\n 3 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("def5678\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      "_actions.archiveChange.destination": { [sourceRel]: oldPrefix },
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.destination).toBe(join(workDir, destinationRel))
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: oldPrefix },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeWhenPersistFails_FailsWithRetrySafeBeforeMove", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")

    const patchRunVars = vi.fn().mockRejectedValue(new Error("server unavailable"))
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.errorCode).toBe("retry-safe")
    expect(output.stage).toBe("persist-name")
    expect(output.source).toBe(changeDir)
  })

  it("ArchiveChangeBackfillsArchiveNameWhenSourceMissingAndArchiveExists", async () => {
    // Source change directory was already moved by a prior run whose
    // `writeVars` never reached the server (or this is the first retry on
    // the new runner). No `openspecArchiveName` is persisted, but the
    // archive directory exists on disk under today's prefix. The action
    // must backfill `openspecArchiveName = basename(archiveDir)` before
    // continuing and must NOT fail with `missing-source`.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const archivedName = `${datePrefix}-issue-127`
    const archivedDir = join(workDir, "openspec", "changes", "archive", archivedName)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${archivedName}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main aaa1111] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("aaa1111\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.destination).toBe(archivedDir)
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: archivedName },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeSubsequentSameRunCrossDateRetry_ReusesBackfilledArchiveName", async () => {
    // Simulates: a prior run's backfill persisted `openspecArchiveName`;
    // a later retry crosses a UTC date boundary. The action must read
    // the backfilled name (which points to the old-date archive) and
    // NOT recompute today's date prefix.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const oldPrefix = "2026-06-25-issue-127"
    const archivedDir = join(workDir, "openspec", "changes", "archive", oldPrefix)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${oldPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main bbb2222] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("bbb2222\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      openspecArchiveName: oldPrefix,
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.destination).toBe(archivedDir)
    expect(output.errorCode).toBeNull()
    expect(patchRunVars).not.toHaveBeenCalled()
  })

  it("ArchiveChangeBackfillPersistFailure_ReturnsRetrySafePersistNameWithoutMove", async () => {
    // Source is missing, an existing archive is found by `findExistingArchive`,
    // but the `writeVars` call to backfill `openspecArchiveName` rejects.
    // The action must return `persist-name` retry-safe and must NOT touch
    // the existing archive (the basename is the same, but the failed persist
    // means a retry will re-attempt the persist).
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const archivedDir = join(workDir, "openspec", "changes", "archive", `${datePrefix}-issue-127`)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const patchRunVars = vi.fn().mockRejectedValue(new Error("server unavailable"))
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.errorCode).toBe("retry-safe")
    expect(output.stage).toBe("persist-name")
    expect(output.source).toBe(changeDir)
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: `${datePrefix}-issue-127` },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeLegacyOnlyFirstRun_MigratesToOpenspecArchiveNameOnBeforeMove", async () => {
    // A pre-existing in-flight run only has the legacy
    // `_actions.archiveChange.destination` key set. The new code must read
    // the legacy basename and migrate it to `openspecArchiveName` at the
    // before-move write site, so a subsequent retry uses the new key.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const oldPrefix = "2026-06-25-issue-127"
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${oldPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n${destinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main ccc3333] Archive OpenSpec change: issue-127\n 3 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("ccc3333\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      "_actions.archiveChange.destination": { [sourceRel]: oldPrefix },
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.destination).toBe(join(workDir, destinationRel))
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: oldPrefix },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeBothKeysPresent_PrefersOpenspecArchiveNameAndIgnoresLegacy", async () => {
    // When both `openspecArchiveName` and the legacy nested-map entry are
    // present, the action must prefer the new key for archive-name
    // resolution (D2 priority order) and ignore the legacy value. The
    // action must NOT write any variable in this scenario because the
    // archive at the persisted (new-key) destination already exists.
    const workDir = await mkdtemp(join(tmpdir(), "mohist-archive-change-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const newPrefix = "2026-06-26-issue-127"
    const legacyPrefix = "2026-06-25-issue-127"
    const newArchivedDir = join(workDir, "openspec", "changes", "archive", newPrefix)
    await mkdir(newArchivedDir, { recursive: true })
    await writeFile(join(newArchivedDir, "proposal.md"), "new-key archive\n")

    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${newPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main ddd4444] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("ddd4444\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      openspecArchiveName: newPrefix,
      "_actions.archiveChange.destination": { [sourceRel]: legacyPrefix },
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.destination).toBe(newArchivedDir)
    expect(patchRunVars).not.toHaveBeenCalled()
  })
})

describe("mohist/openspec-artifacts", () => {
  it("registers openspec-artifacts in the default registry", () => {
    const registry = createDefaultRegistry()
    expect(registry.resolve("mohist/openspec-artifacts")).toBe(openspecArtifactsAction)
  })

  it("returns success and lists zero missing artifacts when all four plan artifacts exist", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs", "pr-first-workflow"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "pr-first-workflow", "spec.md"), "spec\n")
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(result.message).toMatch(/OpenSpec artifacts present under /)
    expect(output.kind).toBe("openspec-artifacts")
    expect(output.changeDir).toBe(changeDir)
    expect(output.present).toBe(true)
    expect(output.missing).toEqual([])
  })

  it("returns failure listing only proposal.md when proposal.md is missing", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs", "pr-first-workflow"), { recursive: true })
    await writeFile(join(changeDir, "specs", "pr-first-workflow", "spec.md"), "spec\n")
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain("OpenSpec artifacts missing")
    expect(result.message).toContain(join(changeDir, "proposal.md"))
    expect(output.present).toBe(false)
    expect(output.missing).toEqual([join(changeDir, "proposal.md")])
  })

  it("returns success when specs directory is missing (specs is optional)", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(changeDir, { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.present).toBe(true)
    expect(output.missing).toEqual([])
  })

  it("returns failure listing only design.md when design.md is missing", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs", "pr-first-workflow"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "pr-first-workflow", "spec.md"), "spec\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain(join(changeDir, "design.md"))
    expect(output.present).toBe(false)
    expect(output.missing).toEqual([join(changeDir, "design.md")])
  })

  it("returns failure listing only tasks.json when tasks.json is missing", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs", "pr-first-workflow"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "pr-first-workflow", "spec.md"), "spec\n")
    await writeFile(join(changeDir, "design.md"), "design\n")

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain(join(changeDir, "tasks.json"))
    expect(output.present).toBe(false)
    expect(output.missing).toEqual([join(changeDir, "tasks.json")])
  })

  it("returns failure listing every missing artifact when changeDir is empty", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(changeDir, { recursive: true })

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.present).toBe(false)
    expect(output.missing).toEqual([
      join(changeDir, "proposal.md"),
      join(changeDir, "design.md"),
      join(changeDir, "tasks.json"),
    ])
    expect(result.message).toContain(join(changeDir, "proposal.md"))
    expect(result.message).toContain(join(changeDir, "design.md"))
    expect(result.message).toContain(join(changeDir, "tasks.json"))
  })

  it("fails with a clear message when only changeDir is supplied (the action's sole input)", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const result = await openspecArtifactsAction({
      workflowRunId: "workflow-1",
      workId: "plan-artifacts",
      workType: "task",
      stage: "plan",
      title: "Verify plan artifacts",
      uses: "mohist/openspec-artifacts",
      with: {} as never,
      variables: {} as never,
      workDir,
      signal: new AbortController().signal,
      writeVars: vi.fn(),
    })
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toMatch(/requires 'changeDir'/)
    expect(output.kind).toBeUndefined()
  })

  it("ignores other inputs beyond changeDir (only changeDir is consulted)", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-openspec-artifacts-"))
    const changeDir = join(workDir, "openspec", "changes", "issue-270")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "design.md"), "design\n")
    await writeFile(join(changeDir, "tasks.json"), JSON.stringify({ tasks: [] }))

    const result = await openspecArtifactsAction(artifactsContext(workDir, changeDir, {
      path: "/somewhere/else/should/be/ignored",
      extra: "noise",
    }))

    expect(result.status).toBe("success")
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
    writeVars: vi.fn(),
  }
}

function syncContext(workDir: string, changeDir: string): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:spec-sync.1",
    workType: "task",
    stage: "integrate",
    title: "Sync OpenSpec specs",
    uses: "mohist/openspec-sync",
    with: { changeDir } as never,
    variables: {} as never,
    workDir,
    signal: new AbortController().signal,
    writeVars: vi.fn(),
  }
}

function archiveContext(workDir: string, changeDir: string, variables: JsonObject = {}, serverConnection?: Partial<ServerConnection>): ActionContext {
  const signal = new AbortController().signal
  const patchRunVars = serverConnection?.patchRunVars ?? vi.fn()
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:archive-change.1",
    workType: "task",
    stage: "integrate",
    title: "Archive change",
    uses: "mohist/archive-change",
    with: { changeDir } as never,
    variables: variables as never,
    workDir,
    signal,
    serverConnection: serverConnection as ServerConnection | undefined,
    writeVars: async (vars) => patchRunVars("workflow-1", vars, signal),
  }
}

function artifactsContext(workDir: string, changeDir: string, extra: Record<string, unknown> = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "plan-artifacts",
    workType: "task",
    stage: "plan",
    title: "Verify plan artifacts",
    uses: "mohist/openspec-artifacts",
    with: { changeDir, ...extra } as never,
    variables: {} as never,
    workDir,
    signal: new AbortController().signal,
    writeVars: vi.fn(),
  }
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 1) {
  return { success: false, stdout: "", stderr, exitCode, combinedOutput: stderr }
}
