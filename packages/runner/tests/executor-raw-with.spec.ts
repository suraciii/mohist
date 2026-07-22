import { mkdtemp, rm } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import type { JsonObject, DispatchWorkItem } from "../src/core/types.js"
import type { ActionHost } from "../src/actions/host.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setExecutorGitRunnerForTest, type GitRunner } from "../src/runtime/git-probe.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"
import { defineTestAction, ActionRegistry } from "./support/action-registry-test.js"

let workDir: string

const nonGitRunner: GitRunner = async () => ({
  success: false,
  stdout: "",
  stderr: "not a git repository",
  exitCode: 128,
  combinedOutput: "not a git repository",
})

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-raw-with-"))
  setExecutorGitRunnerForTest(nonGitRunner)
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

describe("WorkExecutor action input boundary", () => {
  it("exposes only recursively-rendered input to a custom Action", async () => {
    let capturedInputs: JsonObject | null = null
    let capturedHost: ActionHost | null = null

    const registry = new ActionRegistry([
      defineTestAction("test/capture-inputs", async (inputs, host) => {
        capturedInputs = inputs
        capturedHost = host
        return { output: null }
      }, {
        inputs: {
          task: { types: ["object"] },
        },
      }),
    ])

    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      workDir,
    )

    const agentObject = { type: "opencode", model: "openai/gpt-5.4" }
    const placeholder = "${{ vars.agent }}"

    const workItem: DispatchWorkItem = {
      workflowRunId: "wf-raw-with",
      workId: "work-raw-with",
      workType: "task",
      stage: "build",
      title: "Test action input boundary",
      uses: "test/capture-inputs",
      with: { task: { with: { options: placeholder } } },
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: { agent: agentObject },
      },
    }

    const result = await executor.execute(workItem, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(capturedInputs).not.toBeNull()
    expect(capturedHost).not.toBeNull()

    const renderedTask = capturedInputs!.task as JsonObject
    expect((renderedTask as JsonObject).with).toEqual({ options: agentObject })
    expect(capturedInputs).not.toHaveProperty("variables")
    expect(capturedInputs).not.toHaveProperty("rawWith")
    expect(capturedInputs).not.toHaveProperty("rawTask")
    expect(capturedHost!.workDir).toBe(workDir)
  })

  it("receives inputs and host without exposing internal data", async () => {
    let capturedInputs: JsonObject | null = null
    let capturedHost: ActionHost | null = null
    const registry = new ActionRegistry([
      defineTestAction("test/capture-inputs-boundary", async (inputs, host) => {
        capturedInputs = inputs
        capturedHost = host
        return { output: null }
      }, {
        inputs: {
          prompt: { types: ["string"] },
        },
      }),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      workDir,
    )
    const workItem: DispatchWorkItem = {
      workflowRunId: "wf-parent-context",
      workId: "work-parent-context",
      workType: "task",
      stage: "plan",
      uses: "test/capture-inputs-boundary",
      with: { prompt: "child prompt" },
      variables: { workspace: { path: workDir, branch: null, changeDir: null } },
      parentIssueContext: { title: "Parent", body: "Parent body" },
    }

    const result = await executor.execute(workItem, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(capturedInputs).toEqual({ prompt: "child prompt" })
    expect(capturedHost!.workDir).toBe(workDir)
    expect(capturedInputs).not.toHaveProperty("variables")
    expect(capturedHost).not.toHaveProperty("variables")
  })

  it("derives engine-sourced inputs from variables without exposing the variable map", async () => {
    let capturedInputs: JsonObject | null = null
    const registry = new ActionRegistry([
      defineTestAction("test/engine-input", async (inputs) => {
        capturedInputs = inputs
        return { output: null }
      }, {
        inputs: {
          buildPrompt: { types: ["string"], engineSource: "prompts.build" },
        },
      }),
    ])

    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      workDir,
    )

    const result = await executor.execute({
      workflowRunId: "wf-engine-input",
      workId: "work-engine-input",
      workType: "task",
      title: "Engine input",
      uses: "test/engine-input",
      with: {},
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        prompts: { build: "build instructions" },
      },
    }, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(capturedInputs).toEqual({ buildPrompt: "build instructions" })
    expect(capturedInputs).not.toHaveProperty("variables")
  })
})

describe("Dispatch rendering boundary", () => {
  it("renders immediate nested templates against the carried snapshot", async () => {
    let capturedInputs: JsonObject | null = null
    const registry = new ActionRegistry([
      defineTestAction("test/render-snapshot", async (inputs) => {
        capturedInputs = inputs
        return { output: null }
      }, {
        inputs: {
          prompt: { types: ["string"] },
          options: { types: ["object"] },
        },
      }),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      workDir,
    )
    const rawWith = {
      prompt: "${{ vars.message }}",
      options: { mode: "${{ vars.mode }}", retries: "${{ vars.retries }}" },
    }
    const workItem: DispatchWorkItem = {
      workflowRunId: "wf-render",
      workId: "work-render",
      workType: "task",
      stage: "plan",
      uses: "test/render-snapshot",
      with: rawWith,
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: { message: "do work", mode: "fast", retries: 2 },
      },
    }
    const result = await executor.execute(workItem, new AbortController().signal)
    expect(result.status).toBe("completed")
    expect(capturedInputs).toEqual({
      prompt: "do work",
      options: { mode: "fast", retries: 2 },
    })
    expect(workItem.with).toBe(rawWith)
    expect(workItem.with).toEqual({
      prompt: "${{ vars.message }}",
      options: { mode: "${{ vars.mode }}", retries: "${{ vars.retries }}" },
    })
  })

  it.each([
    ["object", { model: "model-a", variant: "high" }],
    ["array", [1, 2, 3]],
    ["number", 42],
    ["boolean", true],
  ])("preserves whole-value JSON type for a %s reference", async (_label, resolved) => {
    let capturedInputs: JsonObject | null = null
    const registry = new ActionRegistry([
      defineTestAction("test/json-types", async (inputs) => {
        capturedInputs = inputs
        return { output: null }
      }, {
        inputs: {
          agent: { types: ["string", "number", "boolean", "object", "array"] },
        },
      }),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      workDir,
    )
    const workItem: DispatchWorkItem = {
      workflowRunId: "wf-json-types",
      workId: "work-json-types",
      workType: "task",
      stage: "plan",
      uses: "test/json-types",
      with: { agent: "${{ vars.value }}" },
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: { value: resolved },
      },
    }
    const result = await executor.execute(workItem, new AbortController().signal)
    expect(result.status).toBe("completed")
    expect(capturedInputs).toEqual({ agent: resolved })
  })

  it("fails an immediate whole-value reference without invoking the Action", async () => {
    let actionInvoked = false
    const registry = new ActionRegistry([
      defineTestAction("test/missing-ref", async () => {
        actionInvoked = true
        return { output: null }
      }, {
        inputs: { agent: { types: ["object"] } },
      }),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      workDir,
    )
    const workItem: DispatchWorkItem = {
      workflowRunId: "wf-unresolved",
      workId: "work-unresolved",
      workType: "task",
      stage: "plan",
      uses: "test/missing-ref",
      with: { agent: "${{ vars.missing }}" },
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: {},
      },
    }
    const result = await executor.execute(workItem, new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(actionInvoked).toBe(false)
    expect(result.message).toContain("vars.missing")
  })

  it("keeps nested templates inside a deferred field unchanged for the Action", async () => {
    let capturedInputs: JsonObject | null = null
    const registry = new ActionRegistry([
      defineTestAction("test/deferred-tasks", async (inputs) => {
        capturedInputs = inputs
        return { output: null }
      }, {
        inputs: {
          tasks: { types: ["array"], render: "deferred" },
        },
      }),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      workDir,
    )
    const deferredTasks: JsonObject = {
      items: [
        { id: "child-1", uses: "test/echo", with: { agent: "${{ vars.agent }}" } },
        { id: "child-2", uses: "test/echo", with: { message: "literal" } },
      ],
    }
    const workItem: DispatchWorkItem = {
      workflowRunId: "wf-deferred",
      workId: "work-deferred",
      workType: "task",
      stage: "plan",
      uses: "test/deferred-tasks",
      with: { tasks: deferredTasks.items as unknown as JsonObject },
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: { agent: { model: "model-a" } },
      },
    }
    const result = await executor.execute(workItem, new AbortController().signal)
    expect(result.status).toBe("completed")
    expect(capturedInputs).toEqual({ tasks: deferredTasks.items })
    expect((capturedInputs!.tasks as JsonObject[])[0]).toEqual({
      id: "child-1",
      uses: "test/echo",
      with: { agent: "${{ vars.agent }}" },
    })
  })

  it("Action mutation of a deferred reference cannot mutate DispatchWorkItem.with", async () => {
    const originalDeferred: JsonObject = { id: "child", with: { agent: { name: "${{ vars.agent }}" } } }
    const observed: { mutated: JsonObject | null; sourceDeferred: unknown } = { mutated: null, sourceDeferred: null }

    const registry = new ActionRegistry([
      defineTestAction("test/deferred-mutation", async (inputs) => {
        const tasks = inputs.tasks as JsonObject[]
        const first = tasks[0]! as JsonObject
        const innerWith = first.with as JsonObject
        ;(innerWith.agent as JsonObject)["name"] = "MUTATED"
        observed.mutated = JSON.parse(JSON.stringify(inputs))
        return { output: null }
      }, {
        inputs: { tasks: { types: ["array"], render: "deferred" } },
      }),
    ])
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      {} as never,
      workDir,
    )
    const workItem: DispatchWorkItem = {
      workflowRunId: "wf-mutation",
      workId: "work-mutation",
      workType: "task",
      stage: "plan",
      uses: "test/deferred-mutation",
      with: { tasks: [originalDeferred] },
      variables: {
        workspace: { path: workDir, branch: null, changeDir: null },
        vars: { agent: "model-a" },
      },
    }
    const result = await executor.execute(workItem, new AbortController().signal)
    if (result.status !== "completed") {
      throw new Error(`expected completed, got ${result.status}: ${result.message ?? ""}`)
    }

    const sourceTask = (workItem.with!.tasks as JsonObject[])[0]! as JsonObject
    expect((sourceTask.with as JsonObject).agent).toEqual({ name: "${{ vars.agent }}" })
    observed.sourceDeferred = JSON.parse(JSON.stringify(workItem.with!.tasks))

    const capturedTask = (observed.mutated!.tasks as JsonObject[])[0]! as JsonObject
    expect((capturedTask.with as JsonObject).agent).toEqual({ name: "MUTATED" })
    expect(observed.sourceDeferred).toEqual([
      { id: "child", with: { agent: { name: "${{ vars.agent }}" } } },
    ])
  })
})
