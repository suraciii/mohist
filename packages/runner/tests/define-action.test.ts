import { describe, expect, it } from "vitest"
import { defineAction } from "../src/actions/define-action.js"

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
})
