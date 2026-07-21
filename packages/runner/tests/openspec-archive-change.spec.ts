import { createHash } from "node:crypto"
import { mkdir, readFile, rename, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { archiveChangeAction, setArchiveRenameForTest, setOpenSpecGitRunnerForTest } from "../src/actions/openspec.js"
import type { ActionContext } from "../src/core/types.js"
import { createTestTempDir } from "./support/temp-dir.js"

const ARCHIVE_TEST_TIME = new Date("2026-07-11T12:00:00.000Z")

describe("mohist/archive-change", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] })
    vi.setSystemTime(ARCHIVE_TEST_TIME)
  })

  afterEach(() => {
    setArchiveRenameForTest(null)
    setOpenSpecGitRunnerForTest(null)
    vi.useRealTimers()
  })

  it("writes a keyed checkpoint atomically before moving and clears it after commit", async () => {
    const { workDir, changeDir, destinationRel } = await fixture()
    const events: string[] = []
    const git = fakeGit(events, destinationRel, { changedFiles: [`${destinationRel}/proposal.md`], sha: "abc1234" })
    setOpenSpecGitRunnerForTest(git)
    setArchiveRenameForTest(async (source, destination) => {
      events.push(`rename:${source.replace(`${workDir}/`, "")}->${destination.replace(`${workDir}/`, "")}`)
      await rename(source, destination)
    })

    const result = await archiveChangeAction(context(workDir, changeDir))
    const checkpointPath = await checkpointPathFor(workDir, "workflow-1", "openspec/changes/issue-127")

    expect(result.error).toBeUndefined()
    expect((result.output as Record<string, unknown>).destination).toBe(join(workDir, destinationRel))
    expect(events[0]).toMatch(/^git-path:/)
    expect(events).toContain(`rename:openspec/changes/issue-127->${destinationRel}`)
    await expect(readFile(checkpointPath, "utf8")).rejects.toMatchObject({ code: "ENOENT" })
  })

  it("resumes a post-move failure from the exact checkpoint destination", async () => {
    const { workDir, changeDir, destinationRel } = await fixture()
    let failCommit = true
    setOpenSpecGitRunnerForTest(fakeGit([], destinationRel, { changedFiles: [`${destinationRel}/proposal.md`], commitFailure: () => failCommit }))

    const first = await archiveChangeAction(context(workDir, changeDir))
    expect(first.error?.code).toBe("retry-safe")
    expect((first.error?.message ?? "")).toContain("git commit archive change failed")

    failCommit = false
    const retry = await archiveChangeAction(context(workDir, changeDir))
    expect(retry.error).toBeUndefined()
    expect((retry.output as Record<string, unknown>).destination).toBe(join(workDir, destinationRel))
  })

  it("reuses a versioned collision destination across a restart", async () => {
    const { workDir, changeDir, baseDestinationRel } = await fixture()
    const destinationRel = `${baseDestinationRel}-v2`
    await mkdir(join(workDir, baseDestinationRel), { recursive: true })
    await writeFile(join(workDir, baseDestinationRel, "old.md"), "old\n")
    const git = fakeGit([], destinationRel, { changedFiles: [`${destinationRel}/proposal.md`], sha: "v2sha" })
    setOpenSpecGitRunnerForTest(git)

    const first = await archiveChangeAction(context(workDir, changeDir))
    expect(first.error).toBeUndefined()
    expect((first.output as Record<string, unknown>).destination).toBe(join(workDir, destinationRel))

    const retry = await archiveChangeAction(context(workDir, changeDir))
    expect(retry.error?.code).toBe("missing-source")
  })

  it.each([
    ["malformed", "not-json"],
    ["wrong version", JSON.stringify({ version: 2, workflowRunId: "workflow-1", source: "openspec/changes/issue-127", destination: "openspec/changes/archive/2026-07-11-issue-127" })],
    ["wrong run", JSON.stringify({ version: 1, workflowRunId: "other", source: "openspec/changes/issue-127", destination: "openspec/changes/archive/2026-07-11-issue-127" })],
    ["escaping destination", JSON.stringify({ version: 1, workflowRunId: "workflow-1", source: "openspec/changes/issue-127", destination: "openspec/changes/archive/../outside" })],
  ])("rejects %s checkpoint before mutation", async (_name, checkpoint) => {
    const { workDir, changeDir } = await fixture()
    const sourceRel = "openspec/changes/issue-127"
    const checkpointPath = await checkpointPathFor(workDir, "workflow-1", sourceRel)
    await mkdir(join(workDir, ".git", "mohist", "archive-change"), { recursive: true })
    await writeFile(checkpointPath, checkpoint)
    const events: string[] = []
    setOpenSpecGitRunnerForTest(fakeGit(events, "openspec/changes/archive/unused"))

    const result = await archiveChangeAction(context(workDir, changeDir))

    expect(result.error?.code).toBe("config-error")
    expect(events.filter((event) => event.startsWith("rename:") || event.startsWith("git:"))).toEqual([])
  })

  it("reports partial archive when checkpoint-bound source and destination both exist", async () => {
    const { workDir, changeDir, destinationRel } = await fixture()
    const checkpointPath = await checkpointPathFor(workDir, "workflow-1", "openspec/changes/issue-127")
    await mkdir(join(workDir, destinationRel), { recursive: true })
    await writeFile(join(workDir, destinationRel, "archive.md"), "archive\n")
    await mkdir(join(workDir, ".git", "mohist", "archive-change"), { recursive: true })
    await writeFile(checkpointPath, JSON.stringify({ version: 1, workflowRunId: "workflow-1", source: "openspec/changes/issue-127", destination: destinationRel }))
    const events: string[] = []
    setOpenSpecGitRunnerForTest(fakeGit(events, destinationRel))

    const result = await archiveChangeAction(context(workDir, changeDir))

    expect(result.error?.code).toBe("partial-archive")
    expect(events.some((event) => event.startsWith("rename:"))).toBe(false)
  })

  it("reports missing source when neither checkpoint-bound path exists", async () => {
    const { workDir, changeDir, destinationRel } = await fixture(false)
    const checkpointPath = await checkpointPathFor(workDir, "workflow-1", "openspec/changes/issue-127")
    await mkdir(join(workDir, ".git", "mohist", "archive-change"), { recursive: true })
    await writeFile(checkpointPath, JSON.stringify({ version: 1, workflowRunId: "workflow-1", source: "openspec/changes/issue-127", destination: destinationRel }))
    setOpenSpecGitRunnerForTest(fakeGit([], destinationRel))

    const result = await archiveChangeAction(context(workDir, changeDir))

    expect(result.error?.code).toBe("missing-source")
  })
})

async function fixture(withSource = true) {
  const workDir = await createTestTempDir("mohist-archive-change-")
  const changeDir = join(workDir, "openspec", "changes", "issue-127")
  const destinationRel = "openspec/changes/archive/2026-07-11-issue-127"
  const baseDestinationRel = destinationRel
  if (withSource) {
    await mkdir(changeDir, { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
  }
  return { workDir, changeDir, destinationRel, baseDestinationRel }
}

function fakeGit(events: string[], destinationRel: string, options: { changedFiles?: string[]; sha?: string; commitFailure?: () => boolean } = {}) {
  return async (workDir: string, args: string[]) => {
    if (args[0] === "rev-parse" && args[1] === "--git-path") {
      events.push(`git-path:${args[2]}`)
      return gitOk(`.git/${args[2]}`)
    }
    const key = args.join(" ")
    events.push(`git:${key}`)
    if (key === `add -A ${destinationRel}`) return gitOk("")
    if (key === "rm -rf --cached --ignore-unmatch openspec/changes/issue-127") return gitOk("")
    if (key === `diff --cached --name-only -- openspec/changes/issue-127 ${destinationRel}`) return gitOk(`${(options.changedFiles ?? []).join("\n")}\n`)
    if (key.startsWith(`commit -m Archive OpenSpec change: issue-127 -- openspec/changes/issue-127 ${destinationRel}`)) {
      if (options.commitFailure?.()) return gitFail("hook failed")
      return gitOk("[main abc1234] archive")
    }
    if (key === "rev-parse HEAD") return gitOk(`${options.sha ?? "abc1234"}\n`)
    return gitFail(`unexpected git call: ${key}`)
  }
}

function context(workDir: string, changeDir: string): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:archive-change.1",
    workType: "task",
    stage: "integrate",
    title: "Archive change",
    uses: "mohist/archive-change",
    with: { changeDir },
    variables: {},
    workDir,
    signal: new AbortController().signal,
    writeVars: vi.fn(),
  }
}

async function checkpointPathFor(workDir: string, workflowRunId: string, sourceRel: string) {
  const key = createHash("sha256").update(`${workflowRunId}\0${sourceRel}`).digest("hex")
  return join(workDir, ".git", "mohist", "archive-change", `${key}.json`)
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 1) {
  return { success: false, stdout: "", stderr, exitCode, combinedOutput: stderr }
}
