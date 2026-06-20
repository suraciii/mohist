import { readFile } from "node:fs/promises"
import { resolve } from "node:path"
import { describe, expect, it } from "vitest"

const workflowPath = resolve(
  process.cwd(),
  "../server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml",
)

describe("mohist default workflow profile", () => {
  it("IntegrateStage_UsesRebaseSquashThenPushWithoutPreparePublish", async () => {
    const yaml = await readFile(workflowPath, "utf8")
    const integrate = yaml.slice(yaml.indexOf("  - stage: integrate"))

    expect(integrate).toContain("id: integrate:spec-sync")
    expect(integrate).toContain("id: integrate:archive-change")
    expect(integrate).toContain("id: integrate:rebase")
    expect(integrate).toContain("uses: mohist/rebase")
    expect(integrate).toContain("remote: origin")
    expect(integrate).toContain("squash: true")
    expect(integrate).toContain("message: \"Complete issue #${{ issue.number }}\"")
    expect(integrate).toContain("id: integrate:push")
    expect(integrate).toContain("uses: mohist/push")
    expect(integrate).toContain("source: ${{ workspace.branch }}")
    expect(integrate).toContain("target: ${{ repository.baseBranch }}")
    expect(integrate).not.toContain("mohist/prepare")
    expect(integrate).not.toContain("mohist/publish")
    expect(integrate.indexOf("id: integrate:rebase")).toBeLessThan(integrate.indexOf("id: integrate:push"))
    expect(integrate.indexOf("id: integrate:push")).toBeLessThan(integrate.indexOf("name: health"))
  })
})
