import { describe, expect, it } from "vitest"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { validateActionInput } from "../src/actions/input-validation.js"

describe("local Git Action manifests", () => {
  it("declare the explicit delivery contract", () => {
    const registry = createDefaultRegistry()
    const inputs = (name: string) => {
      const resolved = registry.resolve(name)
      if (resolved.kind !== "definition") throw new Error(`Missing action ${name}`)
      return resolved.definition.manifest.inputs
    }

    expect(inputs("mohist/workspace-prepare")).toMatchObject({ expectedBranch: { required: true } })
    expect(inputs("mohist/rebase")).toMatchObject({ baseBranch: { required: true } })
    expect(inputs("mohist/rebase-status")).toMatchObject({ baseBranch: { required: true } })
    expect(inputs("mohist/merge-ready")).toMatchObject({
      baseBranch: { required: true },
      source: { required: true },
      remote: { required: true },
    })
    expect(inputs("mohist/push")).toMatchObject({
      source: { required: true },
      target: { required: true },
      remote: { required: true },
    })
    expect(inputs("mohist/push")).not.toHaveProperty("baseBranch")
  })

  it.each([
    ["mohist/workspace-prepare", "expectedBranch", {}],
    ["mohist/rebase", "baseBranch", { remote: "origin" }],
    ["mohist/rebase-status", "baseBranch", { remote: "origin" }],
    ["mohist/merge-ready", "baseBranch", { source: "feature", remote: "origin" }],
    ["mohist/push", "source", { target: "master", remote: "origin" }],
  ])("rejects missing %s input before execution", (name, field, withInput) => {
    const resolved = createDefaultRegistry().resolve(name)
    if (resolved.kind !== "definition") throw new Error(`Missing action ${name}`)

    const result = validateActionInput(resolved.definition.manifest, withInput)

    expect(result).toMatchObject({
      kind: "failure",
      error: { code: "invalid-input", message: expect.stringContaining(`'${field}'`) },
    })
  })

  it("keeps engine-sourced OpenSpec inputs out of the public catalog", () => {
    const entry = createDefaultRegistry().catalog().actions.find((action) => action.name === "mohist/openspec-tasks")
    expect(entry?.inputs.map((input) => input.name)).not.toContain("buildPrompt")
  })
})
