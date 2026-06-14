import { describe, expect, it } from "vitest"
import { captureOutputs } from "../src/runtime/output-capture.js"
import type { ActionResult } from "../src/core/types.js"

function success(output: unknown): ActionResult {
  return { status: "success", output: JSON.stringify(output) }
}

function failure(output: unknown): ActionResult {
  return { status: "failure", output: JSON.stringify(output) }
}

describe("captureOutputs", () => {
  it("extracts declared outputs from successful action result", () => {
    const result = captureOutputs(
      [
        { name: "openspecName", from: "output.openspecName" },
        { name: "changeDir", from: "output.changeDir" },
      ],
      success({ openspecName: "issue-97", changeDir: "openspec/changes/issue-97" }),
    )

    expect(result).toEqual({
      openspecName: "issue-97",
      changeDir: "openspec/changes/issue-97",
    })
  })

  it("returns undefined when task failed", () => {
    const result = captureOutputs(
      [{ name: "openspecName", from: "output.openspecName" }],
      failure({ openspecName: "issue-97" }),
    )

    expect(result).toBeUndefined()
  })

  it("skips missing from fields and captures the rest", () => {
    const result = captureOutputs(
      [
        { name: "present", from: "output.present" },
        { name: "missing", from: "output.missing" },
      ],
      success({ present: "value" }),
    )

    expect(result).toEqual({ present: "value" })
  })

  it("captures only declared outputs", () => {
    const result = captureOutputs(
      [{ name: "declared", from: "output.declared" }],
      success({ declared: "yes", extra: "no" }),
    )

    expect(result).toEqual({ declared: "yes" })
  })

  it("returns undefined when no outputs are declared", () => {
    const result = captureOutputs(undefined, success({ value: "x" }))
    expect(result).toBeUndefined()
  })

  it("returns undefined when action output is not valid JSON", () => {
    const result = captureOutputs(
      [{ name: "x", from: "output.x" }],
      { status: "success", output: "not-json" },
    )
    expect(result).toBeUndefined()
  })

  it("captures nested object values as JSON values", () => {
    const result = captureOutputs(
      [{ name: "config", from: "output.config" }],
      success({ config: { path: "specs", enabled: true } }),
    )

    expect(result).toEqual({ config: { path: "specs", enabled: true } })
  })
})
