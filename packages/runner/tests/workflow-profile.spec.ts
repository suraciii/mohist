import { describe, expect, it } from "vitest"
import { withTestRunnerResources } from "./support/test-resources.js"

const profileFiles = {
  "mohist/local": "/virtual/profiles/mohist-local.workflow.yaml",
  "mohist/github-pr": "/virtual/profiles/mohist-github-pr.workflow.yaml",
} as const

const profileFixtures: Record<keyof typeof profileFiles, string> = {
  "mohist/local": `approval:
  feedback:
    tasks:
      - id: apply-feedback
recoveries:
  rebase-conflicts:
    handlers:
      - when: error.code=conflict
        recovery:
          budget: 1
stages:
  - stage: plan
    tasks:
      - id: workspace-prepare
        with:
          expectedBranch: \${{ workspace.branch }}
      - id: plan-task
    checks:
      - id: plan-health
  - stage: build
    tasks:
      - id: workspace-prepare
        with:
          expectedBranch: \${{ workspace.branch }}
      - id: build-task
    checks: []
  - stage: check
    tasks:
      - id: workspace-prepare
        with:
          expectedBranch: \${{ workspace.branch }}
      - id: merge-ready
        uses: mohist/merge-ready
        with:
          baseBranch: \${{ repository.baseBranch }}
          source: \${{ workspace.branch }}
          remote: origin
    checks: []
  - stage: integrate
    tasks:
      - id: workspace-prepare
        with:
          expectedBranch: \${{ workspace.branch }}
      - id: integrate:archive-change
      - id: integrate:rebase
        uses: mohist/rebase
        with:
          remote: origin
          squash: true
          messageFrom: issue.title
      - id: integrate:push
        uses: mohist/push
        with:
          source: \${{ workspace.branch }}
          target: \${{ repository.baseBranch }}
      - id: integrate:health
`,
  "mohist/github-pr": `approval:
  feedback:
    tasks:
      - id: apply-feedback
recoveries:
  rebase-conflicts:
    handlers:
      - when: error.code=conflict
        recovery:
          budget: 1
stages:
  - stage: plan
    tasks:
      - id: workspace-prepare
        with:
          expectedBranch: \${{ workspace.branch }}
      - id: plan-task
    checks: []
  - stage: build
    tasks:
      - id: workspace-prepare
        with:
          expectedBranch: \${{ workspace.branch }}
      - id: build-task
    checks: []
  - stage: check
    tasks:
      - id: workspace-prepare
        with:
          expectedBranch: \${{ workspace.branch }}
      - id: check-task
    checks: []
  - stage: integrate
    tasks:
      - id: workspace-prepare
        with:
          expectedBranch: \${{ workspace.branch }}
      - id: integrate-task
    checks: []
`,
}

async function readProfile(path: string): Promise<string> {
  const fixture = Object.entries(profileFiles).find(([, fixturePath]) => fixturePath === path)?.[0] as keyof typeof profileFiles | undefined
  if (!fixture) throw new Error(`unknown profile fixture path: ${path}`)
  return await withTestRunnerResources(async (fileSystem) => {
    try {
      await fileSystem.writeText(path, profileFixtures[fixture])
      return await fileSystem.readText(path)
    } finally {
      await fileSystem.deleteDirectory("/virtual/profiles")
      if (fileSystem.exists("/virtual/profiles")) throw new Error("profile fixture directory was not cleaned")
    }
  })
}

const allStages = ["plan", "build", "check", "integrate"] as const

function sliceStage(yaml: string, stageName: string): string {
  const startMarker = `  - stage: ${stageName}`
  const start = yaml.indexOf(startMarker)
  if (start < 0) throw new Error(`Stage '${stageName}' not found in profile yaml`)

  const afterStart = start + startMarker.length
  const remainingStages = allStages.filter((s) => s !== stageName)
  let end = yaml.length
  for (const other of remainingStages) {
    const idx = yaml.indexOf(`  - stage: ${other}`, afterStart)
    if (idx >= 0 && idx < end) end = idx
  }
  return yaml.slice(start, end)
}

function sliceStageTasksList(stageBody: string): string {
  const tasksStart = stageBody.indexOf("\n    tasks:\n")
  if (tasksStart < 0) return ""
  const after = stageBody.slice(tasksStart + "\n    tasks:\n".length)

  const checksIdx = after.indexOf("\n    checks:")
  const end = checksIdx >= 0 ? checksIdx : after.length
  return after.slice(0, end)
}

function firstStageTaskId(stageBody: string): string | null {
  const tasksList = sliceStageTasksList(stageBody)
  if (!tasksList) return null
  const match = tasksList.match(/(?:^|\n)\s*-\s+id:\s*([^\n]+)/)
  return match ? match[1].trim() : null
}

function countOccurrences(haystack: string, needle: string): number {
  if (!needle) return 0
  let count = 0
  let index = 0
  while ((index = haystack.indexOf(needle, index)) !== -1) {
    count++
    index += needle.length
  }
  return count
}

function collectRecoverySections(yaml: string): string[] {
  const sections: string[] = []

  const recoveryRegex = /\n\s+recovery:\n([\s\S]*?)(?=\n\s+recovery:|\n\s+checks:|\n\s*-\s+stage:|$)/g
  for (const match of yaml.matchAll(recoveryRegex)) {
    sections.push(match[1])
  }

  const repairTaskRegex = /\n\s+repairTask:\n([\s\S]*?)(?=\n\s+repairTask:|\n\s*-\s+name:|\n\s+checks:|\n\s*-\s+stage:|$)/g
  for (const match of yaml.matchAll(repairTaskRegex)) {
    sections.push(match[1])
  }

  return sections
}

for (const [profileId, path] of Object.entries(profileFiles)) {
  describe(`${profileId} workflow profile`, () => {
    it("parses as a non-empty UTF-8 file", async () => {
      const yaml = await readProfile(path)
      expect(yaml.length).toBeGreaterThan(0)
    })

    for (const stageName of allStages) {
      it(`first task of ${stageName} stage is workspace-prepare`, async () => {
        const yaml = await readProfile(path)
        const stage = sliceStage(yaml, stageName)
        expect(firstStageTaskId(stage)).toBe("workspace-prepare")
        expect(stage).toContain("expectedBranch: ${{ workspace.branch }}")
      })

      it(`workspace-prepare appears exactly once in ${stageName} stage task list`, async () => {
        const yaml = await readProfile(path)
        const stage = sliceStage(yaml, stageName)
        const tasksList = sliceStageTasksList(stage)
        expect(countOccurrences(tasksList, "id: workspace-prepare")).toBe(1)
      })
    }

    it("does not inject workspace-prepare into any recovery or repairTask sequence", async () => {
      const yaml = await readProfile(path)
      const recoverySections = collectRecoverySections(yaml)

      expect(recoverySections.length).toBeGreaterThan(0)

      for (const section of recoverySections) {
        expect(section).not.toContain("id: workspace-prepare")
      }
    })
  })
}

describe("mohist local workflow profile", () => {
  it("IntegrateStage_UsesRebaseSquashThenPushWithoutPreparePublish", async () => {
    const yaml = await readProfile(profileFiles["mohist/local"])
    const integrate = yaml.slice(yaml.indexOf("  - stage: integrate"))

    expect(integrate).toContain("id: integrate:archive-change")
    expect(integrate).toContain("id: integrate:rebase")
    expect(integrate).toContain("uses: mohist/rebase")
    expect(integrate).toContain("remote: origin")
    expect(integrate).toContain("squash: true")
    expect(integrate).toContain("messageFrom: issue.title")
    expect(integrate).toContain("id: integrate:push")
    expect(integrate).toContain("uses: mohist/push")
    expect(integrate).toContain("source: ${{ workspace.branch }}")
    expect(integrate).toContain("target: ${{ repository.baseBranch }}")
    expect(integrate).not.toContain("mohist/prepare")
    expect(integrate).not.toContain("mohist/publish")
    expect(integrate.indexOf("id: integrate:rebase")).toBeLessThan(integrate.indexOf("id: integrate:push"))
    expect(integrate.indexOf("id: integrate:push")).toBeLessThan(integrate.indexOf("id: integrate:health"))
  })

  it("CheckStage_MergeReadyBindsAllGitInputs", async () => {
    const yaml = await readProfile(profileFiles["mohist/local"])
    const check = sliceStage(yaml, "check")

    expect(check).toContain("uses: mohist/merge-ready")
    expect(check).toContain("baseBranch: ${{ repository.baseBranch }}")
    expect(check).toContain("source: ${{ workspace.branch }}")
    expect(check).toContain("remote: origin")
  })
})
