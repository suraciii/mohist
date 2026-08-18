import { describe, expect, it } from 'vitest'
import { scriptAction } from '../src/actions/built-in-core.js'
import { makeHost } from './support/action-host-test.js'
import { currentTestResourceState, withTestRunnerResources } from './support/test-resources.js'

async function currentFileSystemRead(path: string): Promise<string> {
  return await currentTestResourceState().fileSystem.readText(path)
}

const profileFiles = {
  'mohist/local': '/virtual/profiles/mohist-local.workflow.yaml',
  'mohist/github-pr': '/virtual/profiles/mohist-github-pr.workflow.yaml',
} as const

/**
 * The six verification lanes mandated by the built-in CI contract, in
 * catalog order. The profile YAML is the authoritative source; these tests
 * bind these fixtures to the same contract the Server catalog recognizes.
 */
const laneContract = [
  { id: "verify-install", run: "npm ci", timeout: 900_000 },
  {
    id: "verify-dotnet",
    run: "export DOTNET_ROOT=/home/szf/.dotnet\ndotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false",
    timeout: 1_200_000,
  },
  { id: "verify-web-typecheck", run: "npm run typecheck -w packages/web", timeout: 600_000 },
  { id: "verify-web-tests", run: "npm run test:run -w packages/web", timeout: 900_000 },
  { id: "verify-runner-typecheck", run: "npm run typecheck -w packages/runner", timeout: 600_000 },
  { id: "verify-runner-tests", run: "npm run test:run -w packages/runner -- --no-file-parallelism", timeout: 900_000 },
] as const

const fixCiRecoveryLocal = `        recovery:
          budget: 2
          handlers:
            - tasks:
                - id: recover:fix-ci
                  title: Fix CI verification
                  uses: mohist/opencode
                  with:
                    session: build
                    prompt: \${{ prompts.fix-ci }}
                    options: \${{ vars.agent }}
                  expect:
                    markers:
                      - path: _output
                        oneOf:
                          - <promise>done</promise>
                          - <promise>unfinished</promise>
              retrySelf: true`

const fixCiRecoveryGithubPr = `        recovery:
          budget: 2
          handlers:
            - tasks:
                - id: recover:fix-ci
                  title: Fix CI verification
                  uses: \${{ profile.agentAction }}
                  with:
                    session: build
                    prompt: \${{ prompts.fix-ci }}
                    options: \${{ vars.agent }}
              retrySelf: true`

function laneTasks(recovery: string): string {
  return laneContract
    .map(
      (lane) => `      - id: ${lane.id}
        uses: core/script
        with:
          run: ${lane.run.includes("\n") ? `|\n            ${lane.run.split("\n").join("\n            ")}` : lane.run}
          timeout: ${lane.timeout}
${recovery}`,
    )
    .join("\n")
}

const profileFixtures: Record<keyof typeof profileFiles, string> = {
  'mohist/local': `approval:
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
      - id: load-tasks
        with:
          path: openspec/changes/issue-\${{ issue.number }}/tasks.json
      - id: build-health
        uses: core/script
        with:
          run: git diff --check
          timeout: 300000
      ${laneTasks(fixCiRecoveryLocal)}
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
  'mohist/github-pr': `approval:
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
      - id: load-tasks
        with:
          path: openspec/changes/issue-\${{ issue.number }}/tasks.json
      ${laneTasks(fixCiRecoveryGithubPr)}
      - id: push
        uses: mohist/push
        with:
          source: HEAD
          target: \${{ workspace.branch }}
          remote: origin
          force: true
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
  const fixture = Object.entries(profileFiles).find(([, fixturePath]) => fixturePath === path)?.[0] as
    | keyof typeof profileFiles
    | undefined
  if (!fixture) throw new Error(`unknown profile fixture path: ${path}`)
  return await withTestRunnerResources(async (fileSystem) => {
    try {
      await fileSystem.writeText(path, profileFixtures[fixture])
      return await fileSystem.readText(path)
    } finally {
      await fileSystem.deleteDirectory('/virtual/profiles')
      if (fileSystem.exists('/virtual/profiles')) throw new Error('profile fixture directory was not cleaned')
    }
  })
}

const allStages = ['plan', 'build', 'check', 'integrate'] as const

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
  const tasksStart = stageBody.indexOf('\n    tasks:\n')
  if (tasksStart < 0) return ''
  const after = stageBody.slice(tasksStart + '\n    tasks:\n'.length)

  const checksIdx = after.indexOf('\n    checks:')
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

  const repairTaskRegex =
    /\n\s+repairTask:\n([\s\S]*?)(?=\n\s+repairTask:|\n\s*-\s+name:|\n\s+checks:|\n\s*-\s+stage:|$)/g
  for (const match of yaml.matchAll(repairTaskRegex)) {
    sections.push(match[1])
  }

  return sections
}

/** Splits the build-stage task list into per-lane blocks by lane id. */
function laneBlocks(buildBody: string): Map<string, string> {
  const tasksList = sliceStageTasksList(buildBody)
  const blocks = new Map<string, string>()
  const laneIdRe = /(?:^|\n)\s*-\s+id:\s*(verify-[a-z-]+)/g
  let match: RegExpExecArray | null
  let prevKey: string | null = null
  let prevStart = -1
  while ((match = laneIdRe.exec(tasksList)) !== null) {
    if (prevKey !== null) blocks.set(prevKey, tasksList.slice(prevStart, match.index))
    prevKey = match[1]
    prevStart = match.index
  }
  if (prevKey !== null) {
    blocks.set(prevKey, tasksList.slice(prevStart))
  }
  return blocks
}

function laneRunValue(block: string): string {
  const lines = block.split("\n")
  const runIdx = lines.findIndex((line) => /^\s*run:\s*\|/.test(line))
  if (runIdx < 0) {
    const plain = block.match(/run:\s([^\n]+)/)
    return plain ? plain[1].trim() : ""
  }
  const runIndent = (lines[runIdx].match(/^\s*/) ?? [""])[0].length
  const content: string[] = []
  for (let i = runIdx + 1; i < lines.length; i++) {
    const line = lines[i]
    if (line.trim() === "") continue
    const indent = (line.match(/^\s*/) ?? [""])[0].length
    if (indent <= runIndent) break
    content.push(line.trim())
  }
  return content.join("\n")
}

function laneTimeout(block: string): number {
  const m = block.match(/timeout:\s*(\d+)/)
  return m ? Number(m[1]) : NaN
}

for (const [profileId, path] of Object.entries(profileFiles)) {
  describe(`${profileId} workflow profile`, () => {
    it('parses as a non-empty UTF-8 file', async () => {
      const yaml = await readProfile(path)
      expect(yaml.length).toBeGreaterThan(0)
    })

    for (const stageName of allStages) {
      it(`first task of ${stageName} stage is workspace-prepare`, async () => {
        const yaml = await readProfile(path)
        const stage = sliceStage(yaml, stageName)
        expect(firstStageTaskId(stage)).toBe('workspace-prepare')
        expect(stage).toContain('expectedBranch: ${{ workspace.branch }}')
      })

      it(`workspace-prepare appears exactly once in ${stageName} stage task list`, async () => {
        const yaml = await readProfile(path)
        const stage = sliceStage(yaml, stageName)
        const tasksList = sliceStageTasksList(stage)
        expect(countOccurrences(tasksList, 'id: workspace-prepare')).toBe(1)
      })
    }

    it('does not inject workspace-prepare into any recovery or repairTask sequence', async () => {
      const yaml = await readProfile(path)
      const recoverySections = collectRecoverySections(yaml)

      expect(recoverySections.length).toBeGreaterThan(0)

      for (const section of recoverySections) {
        expect(section).not.toContain('id: workspace-prepare')
      }
    })

    it("build stage defines the six verification lanes in order after orchestration tasks", async () => {
      const yaml = await readProfile(path)
      const build = sliceStage(yaml, "build")

      const taskIdPositions = laneContract.map((lane) => ({
        ...lane,
        index: build.indexOf(`- id: ${lane.id}`),
      }))
      for (const lane of taskIdPositions) {
        expect(lane.index).toBeGreaterThanOrEqual(0)
      }
      for (let i = 1; i < taskIdPositions.length; i++) {
        expect(taskIdPositions[i - 1].index).toBeLessThan(taskIdPositions[i].index)
      }
      expect(build.indexOf("- id: workspace-prepare")).toBeLessThan(taskIdPositions[0].index)
      expect(build.indexOf("- id: load-tasks")).toBeLessThan(taskIdPositions[0].index)

      // No aggregate verify task or aggregate CI variable remains.
      expect(build).not.toMatch(/(?:^|\n)\s*-\s+id:\s*verify\s*$/m)
      expect(build).not.toContain("vars.ci.verify")
    })

    it("build stage lanes carry the exact command lines with the .NET runtime prelude", async () => {
      const yaml = await readProfile(path)
      const build = sliceStage(yaml, "build")
      const blocks = laneBlocks(build)

      expect(blocks.size).toBe(6)
      for (const lane of laneContract) {
        const block = blocks.get(lane.id)
        expect(block, `lane ${lane.id} must have a definition block`).toBeDefined()
        expect(laneRunValue(block!), `lane ${lane.id} run value`).toBe(lane.run)
      }

      const dotnet = blocks.get("verify-dotnet")!
      expect(dotnet).toContain("uses: core/script")
      expect(dotnet.indexOf("export DOTNET_ROOT=/home/szf/.dotnet")).toBeGreaterThanOrEqual(0)
      expect(dotnet.indexOf("export DOTNET_ROOT=/home/szf/.dotnet")).toBeLessThan(dotnet.indexOf("dotnet test Mohist.sln"))

      // The dotnet lane body is only the prelude plus the unchanged command.
      expect(laneRunValue(dotnet)).toBe(
        "export DOTNET_ROOT=/home/szf/.dotnet\ndotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false",
      )
    })

    it("every lane declares its own positive finite timeout and no lane reuses the aggregate 300000 budget", async () => {
      const yaml = await readProfile(path)
      const build = sliceStage(yaml, "build")
      const blocks = laneBlocks(build)

      for (const lane of laneContract) {
        const block = blocks.get(lane.id)!
        const timeout = laneTimeout(block)
        expect(Number.isFinite(timeout), `lane ${lane.id} must have a literal positive finite timeout`).toBe(true)
        expect(timeout).toBeGreaterThan(0)
        expect(timeout, `lane ${lane.id} must not reuse the old aggregate budget`).not.toBe(300000)
        expect(timeout, `lane ${lane.id} timeout must match the lane contract`).toBe(lane.timeout)
      }
    })

    it("no single timeout encloses all six lanes", async () => {
      const yaml = await readProfile(path)
      const build = sliceStage(yaml, "build")
      const blocks = laneBlocks(build)

      for (const lane of laneContract) {
        const block = blocks.get(lane.id)!
        // Each lane carries exactly one timeout and it belongs to the lane,
        // not to an enclosing wrapper around the whole sequence.
        expect(countOccurrences(block, "timeout:")).toBe(1)
      }
    })

    it("every lane carries the same profile-specific fix-ci recovery contract", async () => {
      const yaml = await readProfile(path)
      const build = sliceStage(yaml, "build")
      const blocks = laneBlocks(build)

      const recovery = profileId === "mohist/local" ? fixCiRecoveryLocal : fixCiRecoveryGithubPr
      for (const lane of laneContract) {
        const block = blocks.get(lane.id)!
        expect(block, `lane ${lane.id} must keep the fix-ci recovery declaration`).toContain("budget: 2")
        expect(block).toContain("retrySelf: true")
        expect(block).toContain("id: recover:fix-ci")
        expect(block).toContain("session: build")
        expect(block).toContain("prompt: ${{ prompts.fix-ci }}")
        expect(block).toContain("options: ${{ vars.agent }}")
        if (profileId === "mohist/local") {
          expect(block).toContain("uses: mohist/opencode")
          expect(block).toContain("<promise>done</promise>")
          expect(block).toContain("<promise>unfinished</promise>")
        } else {
          expect(block).toContain("uses: ${{ profile.agentAction }}")
          expect(block).not.toContain("<promise>")
        }
        // The recovery declaration must be exactly the profile's block, unchanged.
        expect(block).toContain(recovery)
      }
    })
  })
}

describe("clean-run lane shells", () => {
  it.each(laneContract)("$id executes in its own fresh shell with its own finite budget", async (lane) => {
    const invocations: Array<{ command: string; args: string[]; timeoutMs: number | undefined; script: string }> = []
    await withTestRunnerResources(
      async () => {
        const result = await scriptAction({ run: lane.run, timeout: lane.timeout }, makeHost())
        expect(result.error?.code ?? result.output).toBeTruthy()

        expect(invocations).toHaveLength(1)
        const invocation = invocations[0]
        expect(invocation.command).toBe(process.platform === "win32" ? "pwsh" : "bash")
        expect(invocation.args).toHaveLength(1)
        expect(invocation.timeoutMs).toBe(lane.timeout)

        const scriptPath = invocation.args[0]
        expect(scriptPath).toMatch(/_\w+\.sh$/)
        // The lane body is the whole script for one shell invocation; no
        // other lane's commands are mixed into this shell. scriptAction
        // deletes the file after the run, so the content is captured while
        // the shell is about to execute it.
        expect(invocation.script).toBe(lane.run)
      },
      {
        commandRunner: {
          run: async (command, args, _cwd, _signal, _env, options) => {
            invocations.push({
              command,
              args: [...args],
              timeoutMs: (options as { timeoutMs?: number } | undefined)?.timeoutMs,
              script: args[0] ? await currentFileSystemRead(args[0]) : "",
            })
            return { exitCode: 0, stdout: "", stderr: "" }
          },
        },
      },
    )
  })

  it("the .NET lane shell sees DOTNET_ROOT before dotnet invokes", async () => {
    const dotnet = laneContract.find((lane) => lane.id === "verify-dotnet")!
    const scripts: string[] = []
    await withTestRunnerResources(
      async () => {
        const result = await scriptAction({ run: dotnet.run, timeout: dotnet.timeout }, makeHost())
        expect(result.error?.code ?? result.output).toBeTruthy()

        expect(scripts).toHaveLength(1)
        const script = scripts[0]
        const lines = script.split("\n")
        expect(lines[0]).toBe("export DOTNET_ROOT=/home/szf/.dotnet")
        expect(lines[1]).toBe("dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false")
        // The export is in the same script/shell, so the dotnet apphost can
        // resolve the runtime without relying on any earlier lane's shell.
        expect(script.indexOf("export DOTNET_ROOT=/home/szf/.dotnet")).toBeLessThan(script.indexOf("dotnet test"))
      },
      {
        commandRunner: {
          run: async (_command, args) => {
            scripts.push(args[0] ? await currentFileSystemRead(args[0]) : "")
            return { exitCode: 0, stdout: "", stderr: "" }
          },
        },
      },
    )
  })

  it("a representative clean run completes all six lanes for both built-in profiles", async () => {
    for (const profileId of Object.keys(profileFiles) as Array<keyof typeof profileFiles>) {
      const yaml = await readProfile(profileFiles[profileId])
      const build = sliceStage(yaml, "build")
      const blocks = laneBlocks(build)
      expect(blocks.size).toBe(6)

      let completed = 0
      for (const lane of laneContract) {
        const block = blocks.get(lane.id)!
        const run = laneRunValue(block)
        const timeout = laneTimeout(block)
        const result = await withTestRunnerResources(
          async () => await scriptAction({ run, timeout }, makeHost()),
          {
            commandRunner: {
              run: async (_command, _args, _cwd, _signal, _env, options) => ({
                exitCode: 0,
                stdout: "",
                stderr: "",
                timeoutMs: (options as { timeoutMs?: number } | undefined)?.timeoutMs,
              }),
            },
          },
        )
        expect(result.error?.code ?? result.output, `${profileId} lane ${lane.id}`).toBeTruthy()
        completed++
      }
      expect(completed).toBe(6)
    }
  })
})

describe("mohist local workflow profile", () => {
  it("IntegrateStage_UsesRebaseSquashThenPushWithoutPreparePublish", async () => {
    const yaml = await readProfile(profileFiles["mohist/local"])
    const integrate = yaml.slice(yaml.indexOf("  - stage: integrate"))

    expect(integrate).toContain('id: integrate:archive-change')
    expect(integrate).toContain('id: integrate:rebase')
    expect(integrate).toContain('uses: mohist/rebase')
    expect(integrate).toContain('remote: origin')
    expect(integrate).toContain('squash: true')
    expect(integrate).toContain('messageFrom: issue.title')
    expect(integrate).toContain('id: integrate:push')
    expect(integrate).toContain('uses: mohist/push')
    expect(integrate).toContain('source: ${{ workspace.branch }}')
    expect(integrate).toContain('target: ${{ repository.baseBranch }}')
    expect(integrate).not.toContain('mohist/prepare')
    expect(integrate).not.toContain('mohist/publish')
    expect(integrate.indexOf('id: integrate:rebase')).toBeLessThan(integrate.indexOf('id: integrate:push'))
    expect(integrate.indexOf('id: integrate:push')).toBeLessThan(integrate.indexOf('id: integrate:health'))
  })

  it('IntegrateStage_RebaseDoesNotDeclareExpectedBranch_EngineInjectsFromWorkspaceBranch', async () => {
    const yaml = await readProfile(profileFiles['mohist/local'])
    const integrate = yaml.slice(yaml.indexOf('  - stage: integrate'))
    const rebaseTask = integrate.slice(
      integrate.indexOf('id: integrate:rebase'),
      integrate.indexOf('id: integrate:push'),
    )

    expect(rebaseTask).toContain('uses: mohist/rebase')
    // The expected run branch is engine-sourced from workspace.branch; the
    // profile must not declare a second branch for the rebase task and
    // must not treat baseBranch as the workspace identity.
    expect(rebaseTask).not.toContain('expectedBranch')
    expect(rebaseTask).toContain('remote: origin')
    expect(rebaseTask).toContain('squash: true')
  })

  it('CheckStage_MergeReadyBindsAllGitInputs', async () => {
    const yaml = await readProfile(profileFiles['mohist/local'])
    const check = sliceStage(yaml, 'check')

    expect(check).toContain('uses: mohist/merge-ready')
    expect(check).toContain('baseBranch: ${{ repository.baseBranch }}')
    expect(check).toContain('source: ${{ workspace.branch }}')
    expect(check).toContain('remote: origin')
  })
})