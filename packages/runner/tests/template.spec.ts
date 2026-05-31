import { describe, expect, it } from "vitest"
import { renderTemplate } from "../src/core/template.js"

describe("renderTemplate", () => {
  it("VariableValueContainsTemplate_RendersNestedVariables", () => {
    const rendered = renderTemplate({
      prompt: "${{ prompts.proposal }}",
    }, {
      prompts: {
        proposal: "Write to ${{ openspecChangeDir }}/proposal.md for ${{ issue.title }}",
      },
      openspecChangeDir: "openspec/changes/issue-2",
      issue: {
        title: "Document update smoke validation note after update",
      },
    })

    expect(rendered?.prompt).toBe("Write to openspec/changes/issue-2/proposal.md for Document update smoke validation note after update")
  })

  it("NestedExpansionDoesNotCoerceFullObjectVariables", () => {
    const rendered = renderTemplate({ agent: "${{ vars.agent }}" }, { vars: { agent: { type: "opencode" } } })

    expect(rendered?.agent).toEqual({ type: "opencode" })
  })
})
