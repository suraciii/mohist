import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import type { JsonObject } from "../src/core/types.js"
import {
  archiveChangeAction,
  type ArchiveFileSystem,
  type OpenSpecGitRunner,
} from "../src/actions/openspec.js"
import type { ActionTestContext as ActionContext } from "./support/action-test-context.js"
import { callAction } from "./support/call-action.js"
import { withTestRunnerResources } from "./support/test-resources.js"

const ARCHIVE_TEST_TIME = new Date("2026-07-11T12:00:00.000Z")

describe("mohist/archive-change", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] })
    vi.setSystemTime(ARCHIVE_TEST_TIME)
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it("absent hint: computes a dated destination, moves, commits, and writes the destination", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture(events)

    const result = await withArchiveResources(fileSystem, fakeGit(events, destinationRel, { changedFiles: [`${destinationRel}/proposal.md`], sha: "abc1234" }), () =>
      callAction(archiveChangeAction, context(workDir, changeDir)))

    expect(result.error).toBeUndefined()
    const output = result.output as Record<string, unknown>
    expect(output.destination).toBe(join(workDir, destinationRel))
    expect(output.destinationRel).toBe(destinationRel)
    expect(output.changed).toBe(true)
    expect(events).toContain(`rename:openspec/changes/issue-127->${destinationRel}`)
    expect(await fileSystem.hasFiles(join(workDir, destinationRel))).toBe(true)
    expect(await fileSystem.hasFiles(changeDir)).toBe(false)
    expect(result.effects?.writeVars).toEqual({ archive: destinationRel })
  })

  it("idempotent: hint points at an existing destination and source is gone — no move, no commit, no var rewrite", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture(events, /* withSource */ false)
    // The prior archive already moved source -> destination; seed the destination.
    fileSystem.writeFile(join(workDir, destinationRel, "proposal.md"), "proposal\n")

    const result = await withArchiveResources(fileSystem, fakeGit(events, destinationRel), () =>
      callAction(archiveChangeAction, context(workDir, changeDir, { archiveHint: destinationRel })))

    expect(result.error).toBeUndefined()
    const output = result.output as Record<string, unknown>
    expect(output.noChange).toBe(true)
    expect(output.changed).toBe(false)
    expect(events.some((event) => event.startsWith("rename:"))).toBe(false)
    expect(events.some((event) => event.startsWith("git:"))).toBe(false)
    expect(result.effects?.writeVars).toBeUndefined()
  })

  it("stale hint with source still present re-archives and overwrites the var", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture(events)

    const result = await withArchiveResources(fileSystem, fakeGit(events, destinationRel, { changedFiles: [`${destinationRel}/proposal.md`] }), () =>
      callAction(archiveChangeAction, context(workDir, changeDir, { archiveHint: "openspec/changes/archive/2025-12-31-issue-127" })))

    expect(result.error).toBeUndefined()
    expect(result.effects?.writeVars).toEqual({ archive: destinationRel })
    expect(events).toContain(`rename:openspec/changes/issue-127->${destinationRel}`)
  })

  it("stale hint with neither source nor destination present fails missing-source", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture(events, /* withSource */ false)

    const result = await withArchiveResources(fileSystem, fakeGit(events, destinationRel), () =>
      callAction(archiveChangeAction, context(workDir, changeDir, { archiveHint: destinationRel })))

    expect(result.error?.code).toBe("missing-source")
    expect(events.some((event) => event.startsWith("rename:"))).toBe(false)
  })

  it("no hint and no source fails missing-source (first-time archive without a change directory)", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture(events, /* withSource */ false)

    const result = await withArchiveResources(fileSystem, fakeGit(events, destinationRel), () =>
      callAction(archiveChangeAction, context(workDir, changeDir)))

    expect(result.error?.code).toBe("missing-source")
  })

  it("partial-archive: hint points at existing destination and source also present", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture(events)
    fileSystem.writeFile(join(workDir, destinationRel, "archive.md"), "archive\n")

    const result = await withArchiveResources(fileSystem, fakeGit(events, destinationRel), () =>
      callAction(archiveChangeAction, context(workDir, changeDir, { archiveHint: destinationRel })))

    expect(result.error?.code).toBe("partial-archive")
    expect(events.some((event) => event.startsWith("rename:"))).toBe(false)
  })

  it("commit failure rolls back the move and returns retry-safe (retry starts clean)", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture(events)
    let failCommit = true

    const git = fakeGit(events, destinationRel, { changedFiles: [`${destinationRel}/proposal.md`], commitFailure: () => failCommit })
    const first = await withArchiveResources(fileSystem, git, () => callAction(archiveChangeAction, context(workDir, changeDir)))
    expect(first.error?.code).toBe("retry-safe")
    expect((first.error?.message ?? "")).toContain("git commit archive change failed")
    // Rollback moved the directory back to source.
    expect(events).toContain(`rename:${destinationRel}->openspec/changes/issue-127`)
    expect(await fileSystem.hasFiles(join(workDir, "openspec", "changes", "issue-127"))).toBe(true)
    // No var written on failure.
    expect(first.effects?.writeVars).toBeUndefined()

    failCommit = false
    const retry = await withArchiveResources(fileSystem, git, () => callAction(archiveChangeAction, context(workDir, changeDir)))
    expect(retry.error).toBeUndefined()
    expect(retry.effects?.writeVars).toEqual({ archive: destinationRel })
  })

  it("reuses a versioned collision destination when the base archive slot is taken", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, baseDestinationRel } = fixture(events)
    const destinationRel = `${baseDestinationRel}-v2`
    fileSystem.writeFile(join(workDir, baseDestinationRel, "old.md"), "old\n")

    const result = await withArchiveResources(fileSystem, fakeGit(events, destinationRel, { changedFiles: [`${destinationRel}/proposal.md`], sha: "v2sha" }), () =>
      callAction(archiveChangeAction, context(workDir, changeDir)))

    expect(result.error).toBeUndefined()
    const output = result.output as Record<string, unknown>
    expect(output.destination).toBe(join(workDir, destinationRel))
    expect(result.effects?.writeVars).toEqual({ archive: destinationRel })
  })

  it("no-change commit (already committed on a prior attempt) persists the var without a new commit", async () => {
    const events: string[] = []
    const { fileSystem, workDir, changeDir, destinationRel } = fixture(events)

    const result = await withArchiveResources(fileSystem, fakeGit(events, destinationRel, { changedFiles: [] }), () =>
      callAction(archiveChangeAction, context(workDir, changeDir)))

    expect(result.error).toBeUndefined()
    const output = result.output as Record<string, unknown>
    expect(output.noChange).toBe(true)
    // The var is still written so future reruns treat it as idempotent.
    expect(result.effects?.writeVars).toEqual({ archive: destinationRel })
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

async function withArchiveResources<T>(
  fileSystem: ArchiveFileSystem,
  git: OpenSpecGitRunner,
  operation: () => Promise<T>,
): Promise<T> {
  return await withTestRunnerResources(operation, { archiveFileSystem: fileSystem, openSpecGitRunner: git })
}

function fakeGit(events: string[], destinationRel: string, options: { changedFiles?: string[]; sha?: string; commitFailure?: () => boolean } = {}) {
  return async (_workDir: string, args: string[]) => {
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

function context(workDir: string, changeDir: string, overrides: { archiveHint?: string } = {}): ActionContext {
  const withInput: JsonObject = { changeDir }
  if (overrides.archiveHint !== undefined) withInput.archiveHint = overrides.archiveHint
  return {
    workflowRunId: "workflow-1",
    workId: "integrate:archive-change.1",
    workType: "task",
    stage: "integrate",
    title: "Archive change",
    uses: "mohist/archive-change",
    with: withInput,
    variables: {},
    workDir,
    signal: new AbortController().signal,
    writeVars: vi.fn(),
  }
}

function gitOk(stdout: string) {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function gitFail(stderr: string, exitCode = 1) {
  return { success: false, stdout: "", stderr, exitCode, combinedOutput: stderr }
}
