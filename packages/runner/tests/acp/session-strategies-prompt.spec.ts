import { afterEach, describe, expect, it, vi } from "vitest"
import { acpAgentAction as executeAcpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import { PromptLoaderRegistry, setPromptLoaderRegistryForTest, type PromptLoader } from "../../src/core/prompt.js"
import { createFixture, resetAcpTestHooks } from "./support.js"
import { runWithProviderDefaultModelWarning } from "./session-strategies-test-support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  resetAcpTestHooks()
})

describe("mohist/acp-agent new and ephemeral sessions", () => {
  it("StringPrompt_ActionSendsPromptVerbatimWithoutMarkdownEnvelope", async () => {
    const fixture = createFixture("basic")

    const literal = "Fix the build-stage health failure reported by `git diff --check`.\n\n## Keep this markdown verbatim"
    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: literal }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe(literal)
    expect(sentText).not.toContain("## Mohist Issue Context")
    expect(sentText).not.toContain("## Task Prompt")
  })

  it("StringPrompt_ActionDoesNotInjectIssueTitleOrBody", async () => {
    const fixture = createFixture("basic")

    const literal = "Resolve exactly this declared prompt."
    const issueTitle = "Distinct issue title that must not reach prompt text"
    const issueBody = "Distinct issue body that must not reach prompt text"
    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: literal }, undefined, {
      issueNumber: 138,
      variables: {
        project: { path: "D:/fake/work" },
        issue: {
          number: 138,
          title: issueTitle,
          body: issueBody,
        },
      } as never,
    }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe(literal)
    expect(sentText).not.toContain(issueTitle)
    expect(sentText).not.toContain(issueBody)
  })

  it("StringPromptContainingLiteralTemplateSyntax_IsNotTemplateRenderedBeforeMohistContextWrapper", async () => {
    const fixture = createFixture("basic")

    const literal = "literal ${{ prompts.xxx }} should stay intact"
    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: literal }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toContain(literal)
    expect(sentText).not.toContain("prompts.xxx".replace("xxx", "build"))
  })

  it("ObjectPrompt_ActionSendsRenderedXmlWithoutMarkdownEnvelope", async () => {
    const fixture = createFixture("basic")

    const result = await runWithProviderDefaultModelWarning(fixture.context({
      prompt: {
        artifact: {
          attrs: { id: "build-task" },
          task: "Complete exactly one implementation task.",
          instruction: "Follow acceptance criteria.",
        },
      },
    }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe([
      `<artifact id="build-task">`,
      ``,
      `  <task>Complete exactly one implementation task.</task>`,
      ``,
      `  <instruction>Follow acceptance criteria.</instruction>`,
      ``,
      `</artifact>`,
    ].join("\n"))
    expect(sentText).not.toContain("## Task Prompt")
  })

  it("UsesFormPrompt_ActionResolvesThroughRegisteredLoaderBeforeMohistContextWrapper", async () => {
    const fixture = createFixture("basic")
    const loader = vi.fn<PromptLoader>(async () => "loader produced task prompt")
    const registry = new PromptLoaderRegistry()
    registry.register("fake/loader", loader)
    setPromptLoaderRegistryForTest(registry)

    const result = await runWithProviderDefaultModelWarning(fixture.context({
      prompt: { uses: "fake/loader", with: { file: "tasks.json", taskId: "T-001" } },
    }))

    expect(result.status).toBe("success")
    expect(loader).toHaveBeenCalledTimes(1)
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe("loader produced task prompt")
  })

  it("UsesFormPrompt_LoaderReturningObject_IsRenderedThroughDefaultRenderer", async () => {
    const fixture = createFixture("basic")
    const registry = new PromptLoaderRegistry()
    registry.register("fake/object-loader", async () => ({
      artifact: { task: "rendered from loader" },
    }))
    setPromptLoaderRegistryForTest(registry)

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: { uses: "fake/object-loader" } }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe([
      `<artifact>`,
      ``,
      `  <task>rendered from loader</task>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("UsesFormPrompt_LoaderReceivesContextWithWorkflowVariablesWorkDirWorkIdTitleAndStage", async () => {
    const fixture = createFixture("basic")
    const loader = vi.fn<PromptLoader>(async () => "ok")
    const registry = new PromptLoaderRegistry()
    registry.register("fake/echo-loader", loader)
    setPromptLoaderRegistryForTest(registry)

    const variables = {
      workflow: { name: "build" },
      project: { path: "D:/fake/work" },
    }
    await runWithProviderDefaultModelWarning(fixture.context({
      prompt: { uses: "fake/echo-loader", with: { file: "tasks.json", taskId: "T-001" } },
    }, new AbortController().signal, {
      variables: variables as never,
      stage: "build",
      title: "Build task",
    }))

    expect(loader).toHaveBeenCalledTimes(1)
    const received = loader.mock.calls[0][0]
    expect(received.with).toEqual({ file: "tasks.json", taskId: "T-001" })
    expect(received.variables).toEqual(variables)
    expect(received.workDir).toBe("D:/fake/work")
    expect(received.workId).toBe("work-1")
    expect(received.title).toBe("Build task")
    expect(received.stage).toBe("build")
  })

  it("UsesFormPrompt_LoaderReceivesContextWithNullTitleAndStageWhenAbsent", async () => {
    const fixture = createFixture("basic")
    const loader = vi.fn<PromptLoader>(async () => "ok")
    const registry = new PromptLoaderRegistry()
    registry.register("fake/echo-loader", loader)
    setPromptLoaderRegistryForTest(registry)

    await runWithProviderDefaultModelWarning(fixture.context({ prompt: { uses: "fake/echo-loader" } }, new AbortController().signal, {
      title: null,
      stage: null,
    }))

    expect(loader).toHaveBeenCalledTimes(1)
    const received = loader.mock.calls[0][0]
    expect(received.title).toBeNull()
    expect(received.stage).toBeNull()
  })

  it("MissingPrompt_ActionFailsWithoutSendingSynthesizedPrompt", async () => {
    const fixture = createFixture("basic")

    const result = await executeAcpAgentAction(fixture.context({
      description: "Requeue runnable workflows on server startup.",
      acceptanceCriteria: ["runner can claim recovered work"],
    }))

    expect(result.status).toBe("failure")
    expect(result.message).toBe("ACP agent requires 'prompt'")
    expect(fixture.agent.calls.find((entry) => entry.event === "prompt")).toBeUndefined()
    expect(fixture.agent.calls.find((entry) => entry.event === "initialize")).toBeUndefined()
  })

  it("UnknownPromptLoader_ActionFailsWithClearErrorBeforeAnyAcpInteraction", async () => {
    const fixture = createFixture("basic")
    setPromptLoaderRegistryForTest(new PromptLoaderRegistry())

    const result = await executeAcpAgentAction(fixture.context({ prompt: { uses: "no/such-loader" } }))

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Unknown prompt loader: 'no/such-loader'")
    expect(fixture.agent.calls.find((entry) => entry.event === "initialize")).toBeUndefined()
  })
})
