import { describe, expect, it } from "vitest"
import { parseIssueField } from "../src/actions/issue-fields.js"

describe("issue field source parsing", () => {
  it("reads title and body from mo issue show envelope output", () => {
    const json = JSON.stringify({
      success: true,
      data: {
        title: "Use issue title for squash",
        body: "Use issue body for pull request description",
      },
    })

    expect(parseIssueField(json, "issue.title")).toBe("Use issue title for squash")
    expect(parseIssueField(json, "issue.body")).toBe("Use issue body for pull request description")
  })

  it("also accepts direct issue objects", () => {
    const json = JSON.stringify({ title: "Direct title", body: "Direct body" })

    expect(parseIssueField(json, "issue.title")).toBe("Direct title")
    expect(parseIssueField(json, "issue.body")).toBe("Direct body")
  })
})
