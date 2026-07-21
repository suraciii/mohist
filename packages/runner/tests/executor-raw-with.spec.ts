import { mkdtemp, rm } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import type { JsonObject, RenderedWorkItem } from "../src/core/types.js"
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

    const workItem: RenderedWorkItem = {
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
    const workItem: RenderedWorkItem = {
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
