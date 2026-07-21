import { describe, expect, it } from "vitest"
import { ActionRegistry, ActionRegistryConstructionError } from "../src/actions/registry.js"
import type { ActionDefinition } from "../src/actions/manifest.js"

describe("ActionRegistry", () => {
  it("rejects a definition with an invalid manifest even when it bypasses defineAction", () => {
    const definition = {
      manifest: {
        name: "test/action",
        inputs: { value: { types: [] } },
        outputs: [],
        errors: [],
      },
      run: async () => ({ output: null }),
    } as unknown as ActionDefinition

    expect(() => new ActionRegistry([definition])).toThrow(ActionRegistryConstructionError)
  })
})
