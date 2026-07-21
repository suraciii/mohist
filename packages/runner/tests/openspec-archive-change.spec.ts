import { createHash } from "node:crypto"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  archiveChangeAction,
  setArchiveFileSystemForTest,
  setOpenSpecGitRunnerForTest,
  type ArchiveFileSystem,
} from "../src/actions/openspec.js"
import type { ActionContext } from "../src/core/types.js"

const ARCHIVE_TEST_TIME = new Date("2026-07-11T12:00:00.000Z")

describe("mohist/archive-change", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] })
    vi.setSystemTime(ARCHIVE_TEST_TIME)
  })

  afterEach(() => {
    setArchiveFileSystemForTest(null)
    setOpenSpecGitRunnerForTest(null)
    vi.useRealTimers()
  })

  it("writes a keyed checkpoint atomically before moving and clears it after commit", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture(events)
    setArchiveFileSystemForTest(fileSystem)
    setOpenSpecGitRunnerForTest(fakeGit(events, destinationRel, { changedFiles: [`${destinationRel}/proposal.md`], sha: "abc1234" }))

    const result = await archiveChangeAction(context(workDir, changeDir))
    const checkpointPath = checkpointPathFor(workDir, "workflow-1", "openspec/changes/issue-127")

    expect(result.error).toBeUndefined()
    expect((result.output as Record<string, unknown>).destination).toBe(join(workDir, destinationRel))
    expect(events[0]).toMatch(/^git-path:/)
    expect(events).toContain(`rename:openspec/changes/issue-127->${destinationRel}`)
    expect(await fileSystem.exists(checkpointPath)).toBe(false)
  })

  it("resumes a post-move failure from the exact checkpoint destination", async () => {
    const { fileSystem, workDir, changeDir, destinationRel } = fixture()
    let failCommit = true
    setArchiveFileSystemForTest(fileSystem)
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
    const { fileSystem, workDir, changeDir, baseDestinationRel } = fixture()
    const destinationRel = `${baseDestinationRel}-v2`
    fileSystem.writeFile(join(workDir, baseDestinationRel, "old.md"), "old\n")
    setArchiveFileSystemForTest(fileSystem)
    setOpenSpecGitRunnerForTest(fakeGit([], destinationRel, { changedFiles: [`${destinationRel}/proposal.md`], sha: "v2sha" }))

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
    const events: string[] = []
    const { fileSystem, workDir, changeDir } = fixture()
    const sourceRel = "openspec/changes/issue-127"
    const checkpointPath = checkpointPathFor(workDir, "workflow-1", sourceRel)
    await fileSystem.writeAtomic(checkpointPath, checkpoint)
    setArchiveFileSystemForTest(fileSystem)
    setOpenSpecGitRunnerForTest(fakeGit(events, "openspec/changes/archive/unused"))

    const result = await archiveChangeAction(context(workDir, changeDir))

    expect(result.error?.code).toBe("config-error")
    expect(events.filter((event) => event.startsWith("rename:") || event.startsWith("git:"))).toEqual([])
  })

  it("reports partial archive when checkpoint-bound source and destination both exist", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture()
    const checkpointPath = checkpointPathFor(workDir, "workflow-1", "openspec/changes/issue-127")
    fileSystem.writeFile(join(workDir, destinationRel, "archive.md"), "archive\n")
    await fileSystem.writeAtomic(checkpointPath, JSON.stringify({ version: 1, workflowRunId: "workflow-1", source: "openspec/changes/issue-127", destination: destinationRel }))
    setArchiveFileSystemForTest(fileSystem)
    setOpenSpecGitRunnerForTest(fakeGit(events, destinationRel))

    const result = await archiveChangeAction(context(workDir, changeDir))

    expect(result.error?.code).toBe("partial-archive")
    expect(events.some((event) => event.startsWith("rename:"))).toBe(false)
  })

  it("reports missing source when neither checkpoint-bound path exists", async () => {
    const { fileSystem, workDir, changeDir, destinationRel } = fixture([], false)
    const checkpointPath = checkpointPathFor(workDir, "workflow-1", "openspec/changes/issue-127")
    await fileSystem.writeAtomic(checkpointPath, JSON.stringify({ version: 1, workflowRunId: "workflow-1", source: "openspec/changes/issue-127", destination: destinationRel }))
    setArchiveFileSystemForTest(fileSystem)
    setOpenSpecGitRunnerForTest(fakeGit([], destinationRel))

    const result = await archiveChangeAction(context(workDir, changeDir))

    expect(result.error?.code).toBe("missing-source")
  })
})

function fixture(events: string[] = [], withSource = true) {
  const workDir = "/workspace"
  const changeDir = join(workDir, "openspec", "changes", "issue-127")
  const destinationRel = "openspec/changes/archive/2026-07-11-issue-127"
  const fileSystem = new MemoryArchiveFileSystem(events)
  if (withSource) fileSystem.writeFile(join(changeDir, "proposal.md"), "proposal\n")
  return { fileSystem, workDir, changeDir, destinationRel, baseDestinationRel: destinationRel }
}

function fakeGit(events: string[], destinationRel: string, options: { changedFiles?: string[]; sha?: string; commitFailure?: () => boolean } = {}) {
  return async (_workDir: string, args: string[]) => {
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

class MemoryArchiveFileSystem implements ArchiveFileSystem {
  private readonly directories = new Set<string>(["/"])
  private readonly files = new Map<string, string>()

  constructor(private readonly events: string[]) {}

  async exists(path: string): Promise<boolean> {
    return this.directories.has(path) || this.files.has(path)
  }

  async hasFiles(path: string): Promise<boolean> {
    return [...this.files.keys()].some((file) => file.startsWith(`${path}/`))
  }

  async ensureDirectory(path: string): Promise<void> {
    this.addDirectories(path)
  }

  async moveDirectory(source: string, destination: string): Promise<void> {
    this.events.push(`rename:${relativeWorkspacePath(source)}->${relativeWorkspacePath(destination)}`)
    this.addDirectories(destination)
    for (const directory of [...this.directories]) {
      if (directory === source || directory.startsWith(`${source}/`)) {
        this.directories.delete(directory)
        this.directories.add(`${destination}${directory.slice(source.length)}`)
      }
    }
    for (const [path, content] of [...this.files]) {
      if (path === source || path.startsWith(`${source}/`)) {
        this.files.delete(path)
        this.files.set(`${destination}${path.slice(source.length)}`, content)
      }
    }
  }

  async readText(path: string): Promise<string> {
    const content = this.files.get(path)
    if (content === undefined) throw new Error(`Missing file: ${path}`)
    return content
  }

  async writeAtomic(path: string, content: string): Promise<void> {
    this.writeFile(path, content)
  }

  async remove(path: string): Promise<void> {
    this.files.delete(path)
  }

  writeFile(path: string, content: string) {
    this.addDirectories(join(path, ".."))
    this.files.set(path, content)
  }

  private addDirectories(path: string) {
    let current = path
    while (current !== "." && current !== "/") {
      this.directories.add(current)
      current = join(current, "..")
    }
    this.directories.add("/")
  }
}

function relativeWorkspacePath(path: string) {
  return path.replace("/workspace/", "")
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

function checkpointPathFor(workDir: string, workflowRunId: string, sourceRel: string) {
  const key = createHash("sha256").update(`${workflowRunId}\0${sourceRel}`).digest("hex")
  return join(workDir, ".git", "mohist", "archive-change", `${key}.json`)
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 1) {
  return { success: false, stdout: "", stderr, exitCode, combinedOutput: stderr }
}
