import { mkdir, rename, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { archiveChangeAction, setArchiveRenameForTest, setOpenSpecGitRunnerForTest } from "../src/actions/openspec.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { createTestTempDir } from "./support/temp-dir.js"

const ARCHIVE_TEST_TIME = new Date("2026-07-11T12:00:00.000Z")

describe("mohist/archive-change", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] })
    vi.setSystemTime(ARCHIVE_TEST_TIME)
  })

  afterEach(() => {
    setArchiveRenameForTest(null)
    vi.useRealTimers()
  })

  it("ArchiveChangeAfterMove_StagesAndCommitsArchivedChange", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs", "workflow-definition"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "workflow-definition", "spec.md"), "spec\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`
    const destination = join(workDir, destinationRel)
    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk([
          `${destinationRel}/proposal.md`,
          `${destinationRel}/specs/workflow-definition/spec.md`,
        ].join("\n") + "\n")
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main def5678] Archive OpenSpec change: issue-127\n 3 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("def5678\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.kind).toBe("archive-change")
    expect(output.destination).toBe(destination)
    expect(output.changed).toBe(true)
    expect(output.noChange).toBe(false)
    expect(output.commitMessage).toBe("Archive OpenSpec change: issue-127")
    expect(output.commitSha).toBe("def5678")
    expect(output.changedFiles).toEqual([
      `${destinationRel}/proposal.md`,
      `${destinationRel}/specs/workflow-definition/spec.md`,
    ])
    expect(calls.map((args) => args.join(" "))).toEqual([
      `add -A ${destinationRel}`,
      `rm -rf --cached --ignore-unmatch ${sourceRel}`,
      `diff --cached --name-only -- ${sourceRel} ${destinationRel}`,
      `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`,
      "rev-parse HEAD",
    ])
  })

  it("ArchiveChangeRetriedAfterRename_SkipsRenameAndResumesFromStage", async () => {
    // Simulates a previous run that completed the rename but crashed before
    // any git call. The retry must observe the existing archive on disk,
    // skip the rename, and complete the staging + commit.
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`
    const archivedDir = join(workDir, destinationRel)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main abc1234] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("abc1234\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(archivedDir)
    expect(output.changed).toBe(true)
    expect(output.noChange).toBe(false)
    expect(output.commitSha).toBe("abc1234")
    expect(calls.map((args) => args.join(" "))).toEqual([
      `add -A ${destinationRel}`,
      `rm -rf --cached --ignore-unmatch ${sourceRel}`,
      `diff --cached --name-only -- ${sourceRel} ${destinationRel}`,
      `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`,
      "rev-parse HEAD",
    ])
  })

  it("ArchiveChangeRetriedAfterSuccessfulCommit_SkipsCommitAndReturnsNoChange", async () => {
    // Simulates a previous run that completed both the rename and the
    // commit. The retry must observe the empty stage for both source and
    // archive paths and return success without making another commit.
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`
    const archivedDir = join(workDir, destinationRel)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) return gitOk("")
      if (key.startsWith("commit ")) return gitFail(`unexpected commit after retry: ${key}`, 1)
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(archivedDir)
    expect(output.changed).toBe(false)
    expect(output.noChange).toBe(true)
    expect(calls.map((args) => args.join(" "))).toEqual([
      `add -A ${destinationRel}`,
      `rm -rf --cached --ignore-unmatch ${sourceRel}`,
      `diff --cached --name-only -- ${sourceRel} ${destinationRel}`,
    ])
  })

  it("ArchiveChangeRetriedAfterStageBeforeCommit_ResumesFromStage", async () => {
    // Simulates a previous run that crashed between `git add`/`git rm` and
    // `git commit`. The retry must re-run the (idempotent) stage and diff,
    // observe non-empty stage, and successfully commit.
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`
    const archivedDir = join(workDir, destinationRel)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main 9999999] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("9999999\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.commitSha).toBe("9999999")
    expect(calls.map((args) => args.join(" "))).toContain(
      `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`,
    )
  })

  it("ArchiveChangeWhenPersistedDestinationAndSourceBothExist_FailsWithPartialArchive", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const archiveName = `${datePrefix}-issue-127`
    const archivedDir = join(workDir, "openspec", "changes", "archive", archiveName)
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "source proposal\n")
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "archive proposal\n")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      "_actions.archiveChange.destination": { [sourceRel]: archiveName },
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain(changeDir)
    expect(result.error?.message).toContain(archivedDir)
    expect(calls).toEqual([])
  })

  it("ArchiveChangeWhenSourceMissingAndArchiveMissing_FailsWithMissingSource", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeDefined()
    expect(result.error?.message).toMatch(/not found/)
    expect(result.error?.message).toContain(changeDir)
    expect(calls).toEqual([])
  })

  it("ArchiveChangeOnCrossDeviceRename_FallsBackToCopyAndStillCommits", async () => {
    // When the source and destination are on different filesystems, the
    // initial `rename` fails with EXDEV. The action must fall back to a
    // recursive copy + delete and continue with the rest of the flow.
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`

    setArchiveRenameForTest(async () => {
      const err = new Error("EXDEV: cross-device link not permitted") as NodeJS.ErrnoException
      err.code = "EXDEV"
      throw err
    })

    const calls: string[][] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n${destinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main fed4321] Archive OpenSpec change: issue-127\n 2 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("fed4321\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.commitSha).toBe("fed4321")
    expect(calls.map((args) => args.join(" "))).toContain(
      `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`,
    )
  })

  it("ArchiveChangeWithUnrelatedStagedChange_DoesNotIncludeUnrelatedPathInArchiveCommit", async () => {
    // The action must scope its stage/diff/commit to source + archive
    // paths so an unrelated staged change under `openspec/` does not get
    // swept into the archive commit.
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(changeDir, { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`

    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        // Only the archive path appears in the pathspec-filtered diff;
        // an unrelated staged file is filtered out by the pathspec.
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main 1112223] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("1112223\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.changedFiles).toEqual([`${destinationRel}/proposal.md`])
    expect(output.changedFiles.find((file: string) => file.includes("unrelated"))).toBeUndefined()
  })

  it("ArchiveChangeWhenCommitFails_FailsWithStageCommitAndPreservesChangedFiles", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(changeDir, { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`

    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitFail("fatal: cannot commit without a user identity", 128)
      }
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir))
    expect(result.error).toBeDefined()
    expect(result.error?.message).toMatch(/git commit archive change failed/)
    expect(result.error?.message).toContain("cannot commit without a user identity")
  })

  it("ArchiveChangePersistsArchiveNameBeforeMove", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${datePrefix}-issue-127`

    const patchRunVars = vi.fn()
    let writeSeenBeforeMove = false
    setArchiveRenameForTest(async (src, dst) => {
      if (patchRunVars.mock.calls.length > 0) writeSeenBeforeMove = true
      await rename(src, dst)
    })

    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n${destinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main def5678] Archive OpenSpec change: issue-127\n 3 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("def5678\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(join(workDir, destinationRel))
    expect(writeSeenBeforeMove).toBe(true)
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: `${datePrefix}-issue-127` },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeRetryAfterVersionedMove_ReusesExactPersistedDestination", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal v2\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const datePrefix = new Date().toISOString().slice(0, 10)
    const sourceRel = "openspec/changes/issue-127"
    const baseArchiveName = `${datePrefix}-issue-127`
    const versionedArchiveName = `${baseArchiveName}-v2`
    const baseArchiveRel = `openspec/changes/archive/${baseArchiveName}`
    const versionedDestinationRel = `openspec/changes/archive/${versionedArchiveName}`
    const versionedDestination = join(workDir, versionedDestinationRel)
    await mkdir(join(workDir, baseArchiveRel), { recursive: true })
    await writeFile(join(workDir, baseArchiveRel, "proposal.md"), "older archive\n")

    let persistedVars: JsonObject = {}
    const firstPatchRunVars = vi.fn(async (_workflowRunId: string, vars: JsonObject) => {
      persistedVars = { ...persistedVars, ...vars }
    })

    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${versionedDestinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${versionedDestinationRel}`) {
        return gitOk(`${versionedDestinationRel}/proposal.md\n${versionedDestinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${versionedDestinationRel}`) {
        return gitFail("pre-commit hook failed", 1)
      }
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const firstResult = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars: firstPatchRunVars }))
    expect(firstResult.error).toBeDefined()
    expect(firstPatchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: versionedArchiveName },
      expect.any(AbortSignal),
    )

    const retryPatchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${versionedDestinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${versionedDestinationRel}`) {
        return gitOk(`${versionedDestinationRel}/proposal.md\n${versionedDestinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${versionedDestinationRel}`) {
        return gitOk("[main abc1234] Archive OpenSpec change: issue-127\n 2 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("abc1234\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const retryResult = await archiveChangeAction(archiveContext(workDir, changeDir, persistedVars, { patchRunVars: retryPatchRunVars }))
    const retryOutput = JSON.parse(retryResult.output ?? "{}")

    expect(retryResult.error).toBeUndefined()
    expect(retryOutput.destination).toBe(versionedDestination)
    expect(retryOutput.commitSha).toBe("abc1234")
    expect(retryPatchRunVars).not.toHaveBeenCalled()
  })

  it("ArchiveChangeCrossDayRetry_ReusesPersistedNameAndFindsArchivedDirectory", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const oldPrefix = "2026-06-25-issue-127"
    const archivedDir = join(workDir, "openspec", "changes", "archive", oldPrefix)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${oldPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main abc1234] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("abc1234\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      openspecArchiveName: oldPrefix,
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(archivedDir)
    expect(patchRunVars).not.toHaveBeenCalled()
  })

  it.each([
    ["openspecArchiveName", "../escaped"],
    ["openspecArchiveName", "../../escaped"],
    ["openspecArchiveName", "nested/name"],
    ["_actions.archiveChange.destination", "../escaped"],
    ["_actions.archiveChange.destination", "../../escaped"],
    ["_actions.archiveChange.destination", "nested/name"],
  ] as const)("ArchiveChangeRejectsUnsafePersistedName_%s_%s", async (keySource, unsafePrefix) => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")

    const calls: string[][] = []
    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      calls.push(args)
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const variables: JsonObject = keySource === "openspecArchiveName"
      ? { openspecArchiveName: unsafePrefix }
      : { "_actions.archiveChange.destination": { "openspec/changes/issue-127": unsafePrefix } }

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, variables, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeDefined()
    expect(calls).toEqual([])
    expect(patchRunVars).not.toHaveBeenCalled()
  })

  it("ArchiveChangeRetryWithPersistedNameAndNoMove_ReusesNameAndMovesToPersistedDestination", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const oldPrefix = "2026-06-25-issue-127"
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${oldPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n${destinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main def5678] Archive OpenSpec change: issue-127\n 3 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("def5678\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      "_actions.archiveChange.destination": { [sourceRel]: oldPrefix },
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(join(workDir, destinationRel))
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: oldPrefix },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeWhenPersistFails_FailsWithRetrySafeBeforeMove", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")

    const patchRunVars = vi.fn().mockRejectedValue(new Error("server unavailable"))
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeDefined()
  })

  it("ArchiveChangeBackfillsArchiveNameWhenSourceMissingAndArchiveExists", async () => {
    // Source change directory was already moved by a prior run whose
    // `writeVars` never reached the server (or this is the first retry on
    // the new runner). No `openspecArchiveName` is persisted, but the
    // archive directory exists on disk under today's prefix. The action
    // must backfill `openspecArchiveName = basename(archiveDir)` before
    // continuing and must NOT fail with `missing-source`.
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const archivedName = `${datePrefix}-issue-127`
    const archivedDir = join(workDir, "openspec", "changes", "archive", archivedName)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${archivedName}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main aaa1111] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("aaa1111\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(archivedDir)
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: archivedName },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeSubsequentSameRunCrossDateRetry_ReusesBackfilledArchiveName", async () => {
    // Simulates: a prior run's backfill persisted `openspecArchiveName`;
    // a later retry crosses a UTC date boundary. The action must read
    // the backfilled name (which points to the old-date archive) and
    // NOT recompute today's date prefix.
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const oldPrefix = "2026-06-25-issue-127"
    const archivedDir = join(workDir, "openspec", "changes", "archive", oldPrefix)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${oldPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main bbb2222] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("bbb2222\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      openspecArchiveName: oldPrefix,
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(archivedDir)
    expect(patchRunVars).not.toHaveBeenCalled()
  })

  it("ArchiveChangeBackfillPersistFailure_ReturnsRetrySafePersistNameWithoutMove", async () => {
    // Source is missing, an existing archive is found by `findExistingArchive`,
    // but the `writeVars` call to backfill `openspecArchiveName` rejects.
    // The action must return `persist-name` retry-safe and must NOT touch
    // the existing archive (the basename is the same, but the failed persist
    // means a retry will re-attempt the persist).
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const datePrefix = new Date().toISOString().slice(0, 10)
    const archivedDir = join(workDir, "openspec", "changes", "archive", `${datePrefix}-issue-127`)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const patchRunVars = vi.fn().mockRejectedValue(new Error("server unavailable"))
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      return gitFail(`unexpected git call: ${args.join(" ")}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {}, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeDefined()
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: `${datePrefix}-issue-127` },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeLegacyOnlyFirstRun_MigratesToOpenspecArchiveNameOnBeforeMove", async () => {
    // A pre-existing in-flight run only has the legacy
    // `_actions.archiveChange.destination` key set. The new code must read
    // the legacy basename and migrate it to `openspecArchiveName` at the
    // before-move write site, so a subsequent retry uses the new key.
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    await mkdir(join(changeDir, "specs"), { recursive: true })
    await writeFile(join(changeDir, "proposal.md"), "proposal\n")
    await writeFile(join(changeDir, "specs", "spec.md"), "spec\n")

    const oldPrefix = "2026-06-25-issue-127"
    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${oldPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n${destinationRel}/specs/spec.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main ccc3333] Archive OpenSpec change: issue-127\n 3 files changed")
      }
      if (key === "rev-parse HEAD") return gitOk("ccc3333\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      "_actions.archiveChange.destination": { [sourceRel]: oldPrefix },
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(join(workDir, destinationRel))
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: oldPrefix },
      expect.any(AbortSignal),
    )
  })

  it("ArchiveChangeLegacyOnlyRetryWithArchivePresent_MigratesBeforeGitStaging", async () => {
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const oldPrefix = "2026-06-25-issue-127"
    const archivedDir = join(workDir, "openspec", "changes", "archive", oldPrefix)
    await mkdir(archivedDir, { recursive: true })
    await writeFile(join(archivedDir, "proposal.md"), "proposal\n")

    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${oldPrefix}`

    const patchRunVars = vi.fn()
    const events: string[] = []
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      events.push(key)
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main eee5555] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("eee5555\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      "_actions.archiveChange.destination": { [sourceRel]: oldPrefix },
    }, { patchRunVars: async (...args) => {
      events.push("patchRunVars")
      return patchRunVars(...args)
    } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(archivedDir)
    expect(patchRunVars).toHaveBeenCalledTimes(1)
    expect(patchRunVars).toHaveBeenCalledWith(
      "workflow-1",
      { openspecArchiveName: oldPrefix },
      expect.any(AbortSignal),
    )
    expect(events[0]).toBe("patchRunVars")
    expect(events).toContain(`add -A ${destinationRel}`)
  })

  it("ArchiveChangeBothKeysPresent_PrefersOpenspecArchiveNameAndIgnoresLegacy", async () => {
    // When both `openspecArchiveName` and the legacy nested-map entry are
    // present, the action must prefer the new key for archive-name
    // resolution (D2 priority order) and ignore the legacy value. The
    // action must NOT write any variable in this scenario because the
    // archive at the persisted (new-key) destination already exists.
    const workDir = await createTestTempDir("mohist-archive-change-")
    const changeDir = join(workDir, "openspec", "changes", "issue-127")
    const newPrefix = "2026-06-26-issue-127"
    const legacyPrefix = "2026-06-25-issue-127"
    const newArchivedDir = join(workDir, "openspec", "changes", "archive", newPrefix)
    await mkdir(newArchivedDir, { recursive: true })
    await writeFile(join(newArchivedDir, "proposal.md"), "new-key archive\n")

    const sourceRel = "openspec/changes/issue-127"
    const destinationRel = `openspec/changes/archive/${newPrefix}`

    const patchRunVars = vi.fn()
    setOpenSpecGitRunnerForTest(async (_dir, args) => {
      const key = args.join(" ")
      if (key === `add -A ${destinationRel}`) return gitOk("")
      if (key === `rm -rf --cached --ignore-unmatch ${sourceRel}`) return gitOk("")
      if (key === `diff --cached --name-only -- ${sourceRel} ${destinationRel}`) {
        return gitOk(`${destinationRel}/proposal.md\n`)
      }
      if (key === `commit -m Archive OpenSpec change: issue-127 -- ${sourceRel} ${destinationRel}`) {
        return gitOk("[main ddd4444] Archive OpenSpec change: issue-127\n 1 file changed")
      }
      if (key === "rev-parse HEAD") return gitOk("ddd4444\n")
      return gitFail(`unexpected git call: ${key}`, 1)
    })

    const result = await archiveChangeAction(archiveContext(workDir, changeDir, {
      openspecArchiveName: newPrefix,
      "_actions.archiveChange.destination": { [sourceRel]: legacyPrefix },
    }, { patchRunVars }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output.destination).toBe(newArchivedDir)
    expect(patchRunVars).not.toHaveBeenCalled()
  })
})

function archiveContext(workDir: string, changeDir: string, variables: JsonObject = {}, serverConnection?: Partial<ServerConnection>): ActionContext {
  const signal = new AbortController().signal
  const patchRunVars = serverConnection?.patchRunVars ?? vi.fn()
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:archive-change.1",
    workType: "task",
    stage: "integrate",
    title: "Archive change",
    uses: "mohist/archive-change",
    with: { changeDir } as never,
    variables: variables as never,
    workDir,
    signal,
    serverConnection: serverConnection as ServerConnection | undefined,
    writeVars: async (vars) => patchRunVars("workflow-1", vars, signal),
  }
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 1) {
  return { success: false, stdout: "", stderr, exitCode, combinedOutput: stderr }
}
