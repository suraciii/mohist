import { describe, expect, it } from "vitest"
import { ActionDefinitionError, defineAction } from "../src/actions/define-action.js"

describe("defineAction", () => {
  it("deep-freezes array and object defaults", () => {
    const definition = defineAction({
      manifest: {
        name: "test/defaults",
        inputs: {
          arguments: { types: ["array"], default: ["first"] },
          options: { types: ["object"], default: { nested: { enabled: true } } },
        },
        outputs: [],
        errors: [],
      },
      run: async () => ({ output: null }),
    })

    const argumentsDefault = definition.manifest.inputs.arguments.default as string[]
    const optionsDefault = definition.manifest.inputs.options.default as { nested: { enabled: boolean } }

    expect(Object.isFrozen(argumentsDefault)).toBe(true)
    expect(Object.isFrozen(optionsDefault)).toBe(true)
    expect(Object.isFrozen(optionsDefault.nested)).toBe(true)
    expect(() => argumentsDefault.push("unexpected")).toThrow(TypeError)
    expect(() => { optionsDefault.nested.enabled = false }).toThrow(TypeError)
  })

  it("accepts and freezes the closed capability declaration", () => {
    const definition = defineAction({
      manifest: {
        name: "test/capabilities",
        inputs: {},
        outputs: [],
        errors: [],
        capabilities: ["agent-turn", "write-vars"],
      },
      run: async () => ({ output: null }),
    })

    expect(definition.manifest.capabilities).toEqual(["agent-turn", "write-vars"])
    expect(Object.isFrozen(definition.manifest.capabilities)).toBe(true)
  })

  it.each([
    ["unknown capability", ["not-a-capability"] as unknown as never[]],
    ["duplicate capability", ["write-vars", "write-vars"] as never[]],
  ])("rejects %s", (_label, capabilities) => {
    expect(() => defineAction({
      manifest: { name: "test/invalid", inputs: {}, outputs: [], errors: [], capabilities },
      run: async () => ({ output: null }),
    })).toThrow(ActionDefinitionError)
  })

  it("rejects invalid input rendering timing", () => {
    expect(() => defineAction({
      manifest: {
        name: "test/render",
        inputs: { task: { types: ["object"], render: "later" as never } },
        outputs: [],
        errors: [],
      },
      run: async () => ({ output: null }),
    })).toThrow(ActionDefinitionError)
  })
})
