import { describe, expect, it } from "vitest"
import { defineAction } from "../src/actions/define-action.js"
import { normalizeActionResult } from "../src/actions/result-validation.js"
import { capabilitySet } from "../src/actions/host.js"

const manifest = (capabilities?: readonly ("add-tasks" | "write-vars")[]) => defineAction({
  manifest: { name: "test/effects", inputs: {}, outputs: [], errors: [], capabilities },
  run: async () => ({ output: null }),
}).manifest

describe("Action result effects", () => {
  it("rejects effects not authorized by the manifest", () => {
    const result = normalizeActionResult(
      { output: null, effects: { addTasks: [{ id: "follow-up", title: "Follow up" }] } },
      manifest(),
      capabilitySet(manifest()),
    )

    expect(result).toMatchObject({ kind: "malformed", reason: "effects" })
  })

  it("rejects effects on an error result", () => {
    const actionManifest = manifest(["write-vars"])
    const result = normalizeActionResult(
      { error: { code: "unexpected-error", message: "failed" }, effects: { writeVars: { key: true } } },
      actionManifest,
      capabilitySet(actionManifest),
    )

    expect(result).toMatchObject({ kind: "malformed", reason: "effects" })
  })

  it("keeps authorized effects private to normalization", () => {
    const actionManifest = manifest(["add-tasks", "write-vars"])
    const result = normalizeActionResult(
      { output: { loaded: 1 }, effects: { addTasks: [{ id: "follow-up", title: "Follow up" }], writeVars: { key: true } } },
      actionManifest,
      capabilitySet(actionManifest),
    )

    expect(result).toEqual({
      kind: "ok",
      output: { loaded: 1 },
      effects: { addTasks: [{ id: "follow-up", title: "Follow up", uses: null, with: null, expect: null }], writeVars: { key: true } },
    })
  })
})
