import { afterEach, describe, expect, it } from "vitest"
import { setAcpProcessFactoryForTest } from "../src/actions/acp-agent.js"
import { parseOpencodeInput, opencodeAction, OPENCODE_USES } from "../src/actions/opencode.js"
import { createFixture, resetAcpTestHooks } from "./acp/support.js"
import { setPromptLoaderRegistryForTest } from "../src/core/prompt.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  resetAcpTestHooks()
})

describe("mohist/opencode bridge handler", () => {
  describe("parseOpencodeInput (input shape validation)", () => {
    it("OpencodeInput_NoOptions_ReturnsOkWithUndefined", () => {
      const result = parseOpencodeInput({ prompt: "do the work" })
      expect(result.kind).toBe("ok")
      if (result.kind !== "ok") return
      expect(result.options).toBeUndefined()
    })

    it("OpencodeInput_ExplicitNullOptions_ReturnsOkWithUndefined", () => {
      const result = parseOpencodeInput({ prompt: "do the work", options: null as never })
      expect(result.kind).toBe("ok")
    })

    it("OpencodeInput_StringOptions_RejectsAsNonObject", () => {
      const result = parseOpencodeInput({ prompt: "do the work", options: "not-an-object" as never })
      expect(result.kind).toBe("failure")
      if (result.kind !== "failure") return
      expect(result.result.status).toBe("failure")
      expect(result.result.message).toMatch(/options.*must be an object/)
    })

    it("OpencodeInput_ValidModelOnly_Passes", () => {
      const result = parseOpencodeInput({ options: { model: "openai/gpt-5.5" } })
      expect(result.kind).toBe("ok")
      if (result.kind !== "ok") return
      expect(result.options).toEqual({ model: "openai/gpt-5.5" })
    })

    it("OpencodeInput_MultiSegmentModelId_PreservesFullIdAfterFirstSlash", () => {
      const result = parseOpencodeInput({ options: { model: "openrouter/vendor/family/model" } })
      expect(result.kind).toBe("ok")
      if (result.kind !== "ok") return
      expect(result.options?.model).toBe("openrouter/vendor/family/model")
    })

    it("OpencodeInput_ModelMissingProvider_Rejects", () => {
      const result = parseOpencodeInput({ options: { model: "model-only" } })
      expect(result.kind).toBe("failure")
      if (result.kind !== "failure") return
      expect(result.result.message).toMatch(/provider\/model/)
    })

    it("OpencodeInput_ModelMissingModelId_Rejects", () => {
      const result = parseOpencodeInput({ options: { model: "/model" } })
      expect(result.kind).toBe("failure")
      if (result.kind !== "failure") return
      expect(result.result.message).toMatch(/provider\/model/)
    })

    it("OpencodeInput_ModelMissingProviderAtEnd_Rejects", () => {
      const result = parseOpencodeInput({ options: { model: "provider/" } })
      expect(result.kind).toBe("failure")
      if (result.kind !== "failure") return
      expect(result.result.message).toMatch(/provider\/model/)
    })

    it("OpencodeInput_NonStringModel_Rejects", () => {
      const result = parseOpencodeInput({ options: { model: 42 as never } })
      expect(result.kind).toBe("failure")
      if (result.kind !== "failure") return
      expect(result.result.message).toMatch(/options\.model.*must be a string/)
    })

    it("OpencodeInput_VariantOnly_PassesWithoutModel", () => {
      const result = parseOpencodeInput({ options: { variant: "high" } })
      expect(result.kind).toBe("ok")
      if (result.kind !== "ok") return
      expect(result.options).toEqual({ variant: "high" })
    })

    it("OpencodeInput_VariantRemainsSiblingOfModel_NotAppendedToModel", () => {
      const result = parseOpencodeInput({ options: { model: "openai/gpt-5.5", variant: "high" } })
      expect(result.kind).toBe("ok")
      if (result.kind !== "ok") return
      expect(result.options?.model).toBe("openai/gpt-5.5")
      expect(result.options?.variant).toBe("high")
    })

    it("OpencodeInput_NonStringVariant_Rejects", () => {
      const result = parseOpencodeInput({ options: { variant: true as never } })
      expect(result.kind).toBe("failure")
      if (result.kind !== "failure") return
      expect(result.result.message).toMatch(/options\.variant.*must be a string/)
    })

    it("OpencodeInput_UnknownOptionKeys_AreIgnored_NotFailure", () => {
      // Transitional legacy keys (e.g. `type`, liveness settings) MUST NOT
      // make an otherwise valid turn fail.
      const result = parseOpencodeInput({
        options: { model: "openai/gpt-5.5", variant: "high", type: "opencode", livenessQuietThresholdMs: 5000 } as never,
      })
      expect(result.kind).toBe("ok")
      if (result.kind !== "ok") return
      expect(result.options?.model).toBe("openai/gpt-5.5")
      expect(result.options?.variant).toBe("high")
    })

    it("OpencodeInput_NullOptionsWith_EmptyObject_TreatedAsAbsent", () => {
      const result = parseOpencodeInput({ options: {} })
      expect(result.kind).toBe("ok")
      if (result.kind !== "ok") return
      expect(result.options).toEqual({})
    })

    it("OpencodeInput_BlankModel_Rejects", () => {
      const result = parseOpencodeInput({ options: { model: "   " } })
      expect(result.kind).toBe("failure")
      if (result.kind !== "failure") return
      expect(result.result.message).toMatch(/non-empty.*provider\/model/)
    })
  })

  describe("opencodeAction turn delegation and turnFact population", () => {
    it("OpencodeTurn_DelegatesToAcpRuntime_AndPopulatesTurnFact", async () => {
      const fixture = createFixture("basic")

      const result = await opencodeAction(fixture.context({
        prompt: "do the work",
        options: { model: "openai/gpt-5.5" },
      }))

      expect(result.status).toBe("success")
      expect(result.turnFact).toEqual({ finalAssistantText: "hello" })
      const output = JSON.parse(result.output ?? "{}")
      expect(output.kind).toBe("opencode")
      expect(output.model).toBe("openai/gpt-5.5")
      expect(output.variant).toBeNull()
    })

    it("OpencodeTurn_MultiSegmentModelId_DeliveredAsIsToAcp", async () => {
      const fixture = createFixture("basic")

      await opencodeAction(fixture.context({
        prompt: "do the work",
        options: { model: "openrouter/vendor/family/model", variant: "high" },
      }))

      const setModelCall = fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel")
      expect(setModelCall?.modelId).toBe("openrouter/vendor/family/model")
    })

    it("OpencodeTurn_DoesNotAppendVariantToModelId", async () => {
      const fixture = createFixture("basic")

      await opencodeAction(fixture.context({
        prompt: "do the work",
        options: { model: "openai/gpt-5.5", variant: "high" },
      }))

      const setModelCall = fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel")
      // Spec D8: `variant` MUST NOT be appended to or parsed from the
      // model identifier. The model ID is delivered verbatim.
      expect(setModelCall?.modelId).toBe("openai/gpt-5.5")
    })

    it("OpencodeTurn_NoExpectationEvaluation_HappensInExecutor", async () => {
      const fixture = createFixture("basic")

      // Action contract: `expect` is a task-level completion field and
      // never reaches the Action. The handler does not inspect `with.expect`,
      // does not append a repair prompt, and produces no expectation
      // fields in its output.
      const result = await opencodeAction(fixture.context({
        prompt: "review the change",
        expect: { markers: [{ path: "review.md", oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"] }] },
      }))

      expect(result.status).toBe("success")
      expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
      const output = JSON.parse(result.output ?? "{}")
      expect(output.expectation).toBeUndefined()
      expect(output.promise).toBeUndefined()
      expect(output.failIfMarker).toBeUndefined()
    })

    it("OpencodeTurn_PromptOnly_NoAgentKindOrType_Required", async () => {
      const fixture = createFixture("basic")

      // Only prompt is required; `agent`, `kind`, `type` MUST NOT be
      // required by the opencode Action contract.
      const result = await opencodeAction(fixture.context({ prompt: "plain prompt" }))

      expect(result.status).toBe("success")
      expect(result.turnFact?.finalAssistantText).toBe("hello")
    })

    it("OpencodeTurn_BlankPrompt_RejectsBeforeTurn", async () => {
      const fixture = createFixture("basic")

      const result = await opencodeAction(fixture.context({ prompt: "   " }))

      expect(result.status).toBe("failure")
      expect(result.message).toMatch(/requires 'prompt'/)
      expect(fixture.agent.calls.some((entry) => entry.event === "initialize")).toBe(false)
      expect(fixture.agent.calls.some((entry) => entry.event === "newSession")).toBe(false)
    })

    it("OpencodeTurn_MissingPrompt_RejectsBeforeTurn", async () => {
      const fixture = createFixture("basic")

      const result = await opencodeAction(fixture.context({} as never))

      expect(result.status).toBe("failure")
      expect(result.message).toMatch(/requires 'prompt'/)
      expect(fixture.agent.calls.some((entry) => entry.event === "initialize")).toBe(false)
    })

    it("OpencodeTurn_InvalidModelType_RejectsBeforeTurn", async () => {
      const fixture = createFixture("basic")

      const result = await opencodeAction(fixture.context({
        prompt: "do the work",
        options: { model: 42 as never },
      }))

      expect(result.status).toBe("failure")
      expect(result.message).toMatch(/options\.model.*must be a string/)
      expect(fixture.agent.calls.some((entry) => entry.event === "initialize")).toBe(false)
    })

    it("OpencodeTurn_NonProviderSlashModel_RejectsBeforeTurn", async () => {
      const fixture = createFixture("basic")

      const result = await opencodeAction(fixture.context({
        prompt: "do the work",
        options: { model: "no-slash" },
      }))

      expect(result.status).toBe("failure")
      expect(result.message).toMatch(/provider\/model/)
      expect(fixture.agent.calls.some((entry) => entry.event === "initialize")).toBe(false)
    })

    it("OpencodeTurn_OutputCarriesMinimalStructuredShape_NotAcpAgentShape", async () => {
      const fixture = createFixture("basic")

      const result = await opencodeAction(fixture.context({ prompt: "do the work" }))
      const output = JSON.parse(result.output ?? "{}")

      // The opencode contract has its own minimal structured output;
      // it does NOT share `kind: "acp-agent"` with the legacy path.
      expect(output.kind).toBe("opencode")
    })

    it("OpencodeTurn_FailureFromAgentTurn_PropagatesAsFailure_WithTurnFact", async () => {
      const fixture = createFixture("empty-complete")

      const result = await opencodeAction(fixture.context({ prompt: "do the work" }))

      // `empty-complete` scenario produces zero prompt work activity
      // and the ACP runtime returns failure. The opencode bridge
      // delegates that decision unchanged and still populates the
      // turn-fact channel (text is empty for this scenario).
      expect(result.status).toBe("failure")
      expect(result.turnFact).toEqual({ finalAssistantText: "" })
    })
  })

  describe("registry binding", () => {
    it("RegistryResolves_MohistOpencode_ToOpencodeAction", async () => {
      const { createDefaultRegistry } = await import("../src/actions/registry.js")
      const registry = createDefaultRegistry()
      const handler = registry.resolve(OPENCODE_USES)
      expect(handler).toBe(opencodeAction)
    })

    it("RegistryResolves_MohistOpencode_CaseInsensitive", async () => {
      const { createDefaultRegistry } = await import("../src/actions/registry.js")
      const registry = createDefaultRegistry()
      const handler = registry.resolve("Mohist/OpenCode")
      expect(handler).toBe(opencodeAction)
    })

    it("RegistryResolves_MohistAcpAgent_StillRegisteredForLegacy", async () => {
      const { createDefaultRegistry } = await import("../src/actions/registry.js")
      const { acpAgentAction } = await import("../src/actions/acp-agent.js")
      const registry = createDefaultRegistry()
      const handler = registry.resolve("mohist/acp-agent")
      expect(handler).toBe(acpAgentAction)
    })
  })
})