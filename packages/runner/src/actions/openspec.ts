import { createHash } from "node:crypto"
import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path"
import { mkdir, readdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises"
import { exists, copyDirectory, deleteDirectory } from "../system/process.js"
import { git as defaultGit, type GitOptions } from "./git.js"
import type { ActionResult, AddTaskInput, JsonObject, JsonValue } from "../core/types.js"
import type { ActionHost } from "./host.js"
import { isObject, objectInput, stringInput } from "../core/json.js"
import { resolveActionPath } from "./expectations.js"
import { OPENSPEC_TASK_PROMPT_LOADER_NAME } from "./openspec-task-prompt.js"
import { fail, succeed } from "./action-result.js"

const ARCHIVE_CHANGE_COMMIT_MESSAGE_PREFIX = "Archive OpenSpec change"
const ARCHIVE_CHECKPOINT_VERSION = 1

const ACTION_SOURCE = "action:openspec"

function sinkOptions(host: ActionHost): GitOptions | undefined {
  return host.log ? { sink: { log: host.log, source: ACTION_SOURCE } } : undefined
}

export type OpenSpecGitRunner = (workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) => Promise<{
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
}>

let openSpecGitRunner: OpenSpecGitRunner = defaultGit

export function setOpenSpecGitRunnerForTest(runner: OpenSpecGitRunner | null) {
  openSpecGitRunner = runner ?? defaultGit
}

export interface ArchiveFileSystem {
  exists(path: string): Promise<boolean>
  hasFiles(path: string): Promise<boolean>
  ensureDirectory(path: string): Promise<void>
  moveDirectory(source: string, destination: string): Promise<void>
  readText(path: string): Promise<string>
  writeAtomic(path: string, content: string): Promise<void>
  remove(path: string): Promise<void>
}

const defaultArchiveFileSystem: ArchiveFileSystem = {
  exists: async (path) => {
    try {
      await stat(path)
      return true
    } catch {
      return false
    }
  },
  hasFiles,
  ensureDirectory: async (path) => {
    await mkdir(path, { recursive: true })
  },
  moveDirectory: moveChangeDir,
  readText: async (path) => await readFile(path, "utf8"),
  writeAtomic: writeArchiveCheckpoint,
  remove: async (path) => await rm(path, { force: true }),
}

let archiveFileSystem: ArchiveFileSystem = defaultArchiveFileSystem

export function setArchiveFileSystemForTest(fileSystem: ArchiveFileSystem | null) {
  archiveFileSystem = fileSystem ?? defaultArchiveFileSystem
}

async function moveChangeDir(source: string, destination: string) {
  try {
    await rename(source, destination)
  } catch (err) {
    if (!isCrossDeviceError(err)) throw err
    await copyDirectory(source, destination)
    await deleteDirectory(source)
  }
}

function isCrossDeviceError(err: unknown): boolean {
  return Boolean(err && typeof err === "object" && (err as { code?: string }).code === "EXDEV")
}

const DEFAULT_OPENSPEC_ITEMS_PATH = "tasks"

export async function openspecTasksAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const path = resolveActionPath(host.workDir, stringInput(inputs, "path"))
  if (!path) return fail("invalid-input", "OpenSpec task loader requires 'path'")
  if (!exists(path)) return fail("missing-source", `tasks.json not found: ${path}`)

  const root = JSON.parse(await readFile(path, "utf8")) as JsonObject
  const sourceTasks = Array.isArray(root.tasks) ? root.tasks.filter(isObject) : []
  if (!Array.isArray(root.tasks)) return fail("invalid-input", "tasks.json must contain a tasks array")

  const taskDefaults = objectInput(inputs, "task")
  const defaultUses = stringInput(taskDefaults, "uses") ?? "mohist/opencode"
  const defaultWith = objectInput(taskDefaults, "with")
  const itemsPath = stringInput(inputs, "items") ?? DEFAULT_OPENSPEC_ITEMS_PATH
  const tasks: AddTaskInput[] = sourceTasks.flatMap((task) => {
    const id = stringInput(task, "id") ?? stringInput(task, "taskId")
    if (!id?.trim()) return []
    const title = stringInput(task, "title") ?? id
    const uses = stringInput(task, "uses") ?? defaultUses
    const mergedWith = mergeTaskWith(defaultWith, task, id)
    const expect = mergeTaskExpect(task)
    return [{ id, title, uses, with: mergedWith ?? null, expect }]
  })

  if (tasks.length === 0) {
    return succeed({ loaded: 0 })
  }

  return {
    output: { loaded: tasks.length },
    effects: { addTasks: tasks },
  } as unknown as ActionResult
}

export async function openspecArtifactsAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const changeDir = resolveChangeDir(host.workDir, inputs)
  if (!changeDir) return fail("invalid-input", "OpenSpec artifacts check requires 'changeDir'")

  const required: Array<{ path: string; kind: "file" | "directory" }> = [
    { path: join(changeDir, "proposal.md"), kind: "file" },
    { path: join(changeDir, "specs"), kind: "directory" },
    { path: join(changeDir, "design.md"), kind: "file" },
    { path: join(changeDir, "tasks.json"), kind: "file" },
  ]

  const missing: string[] = []
  for (const entry of required) {
    if (!(await isPresentOfKind(entry.path, entry.kind))) missing.push(entry.path)
  }

  const present = missing.length === 0
  const output: JsonObject = {
    kind: "openspec-artifacts",
    changeDir,
    present,
    missing,
  }

  if (present) {
    return succeed(output)
  }

  return fail("artifacts-missing", `OpenSpec artifacts missing under ${changeDir}: ${missing.join(", ")}`)
}

export async function archiveChangeAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const changeDir = resolveChangeDir(host.workDir, inputs)
  if (!changeDir) return archiveFailure("config-error", "Archive change requires 'changeDir'", { kind: "archive-change" })

  const archiveDir = join(dirname(changeDir), "archive")
  const sourceName = basename(changeDir) || "change"
  const sourceRel = relativePath(host.workDir, changeDir)
  const sourceValidation = validateWorkspaceRelativePath(sourceRel)
  if (sourceValidation) return archiveFailure("config-error", sourceValidation, { kind: "archive-change" })

  const checkpoint = await resolveArchiveCheckpoint(host, sourceRel)
  if (checkpoint.kind === "failure") return checkpoint.result

  let destination: string
  const checkpointPath = checkpoint.path
  if (checkpoint.value) {
    const persisted = checkpoint.value
    const destinationValidation = validateCheckpointDestination(host.workDir, archiveDir, persisted.destination)
    if (destinationValidation.kind === "failure") return archiveFailure("config-error", destinationValidation.message, { kind: "archive-change" })
    destination = destinationValidation.path
    const sourcePresent = await archiveFileSystem.exists(changeDir)
    const destinationPresent = await archiveFileSystem.exists(destination)
    if (sourcePresent && destinationPresent) {
      return archiveFailure("partial-archive", `Both source and archive exist; refusing to proceed: source=${changeDir} archive=${destination}`, { kind: "archive-change" })
    }
    if (!sourcePresent && !destinationPresent) {
      return archiveFailure("missing-source", `Change directory not found: ${changeDir}`, { kind: "archive-change" })
    }
    if (sourcePresent) {
      try {
        await archiveFileSystem.moveDirectory(changeDir, destination)
      } catch (err) {
        return archiveFailure("retry-safe", `Failed to move change directory: ${err instanceof Error ? err.message : String(err)}`, { kind: "archive-change" })
      }
    }
  } else {
    if (!(await archiveFileSystem.exists(changeDir)) || !(await archiveFileSystem.hasFiles(changeDir))) {
      return archiveFailure("missing-source", `Change directory not found: ${changeDir}`, { kind: "archive-change" })
    }
    await archiveFileSystem.ensureDirectory(archiveDir)
    const today = new Date().toISOString().slice(0, 10)
    const archivePrefix = `${today}-${sourceName}`
    const resolvedDestination = await uniqueDestination(archiveDir, archivePrefix)
    if (!resolvedDestination) return archiveFailure("config-error", `Archive destination escapes archive root: ${archivePrefix}`, { kind: "archive-change" })
    destination = resolvedDestination
    const destinationRel = relativePath(host.workDir, destination)
    const destinationValidation = validateCheckpointDestination(host.workDir, archiveDir, destinationRel)
    if (destinationValidation.kind === "failure") return archiveFailure("config-error", destinationValidation.message, { kind: "archive-change" })

    if (!host.checkpoint) return archiveFailure("config-error", "Archive change requires the workflow-checkpoint capability", { kind: "archive-change" })
    const workflowToken = await host.checkpoint.token(`archive-change/${sourceRel}`)

    const checkpointValue: ArchiveCheckpoint = {
      version: ARCHIVE_CHECKPOINT_VERSION,
      workflowRunId: workflowToken,
      source: sourceRel,
      destination: destinationRel,
    }
    try {
      await archiveFileSystem.writeAtomic(checkpointPath, `${JSON.stringify(checkpointValue)}\n`)
    } catch (err) {
      return archiveFailure("retry-safe", `Failed to persist archive checkpoint: ${err instanceof Error ? err.message : String(err)}`, { kind: "archive-change" })
    }
    try {
      await archiveFileSystem.moveDirectory(changeDir, destination)
    } catch (err) {
      return archiveFailure("retry-safe", `Failed to move change directory: ${err instanceof Error ? err.message : String(err)}`, { kind: "archive-change" })
    }
  }

  const destinationRel = relativePath(host.workDir, destination)
  const commitMessage = `${ARCHIVE_CHANGE_COMMIT_MESSAGE_PREFIX}: ${sourceName}`
  const opts = sinkOptions(host)

  const addResult = await openSpecGitRunner(host.workDir, ["add", "-A", destinationRel], host.signal, opts)
  if (!addResult.success) {
    return archiveFailure("retry-safe", `git add archive change failed: ${addResult.combinedOutput || addResult.stderr || `exit ${addResult.exitCode}`}`, {
      kind: "archive-change",
      source: changeDir,
      destination,
      stage: "add",
      addOutput: addResult.combinedOutput,
    })
  }

  const rmResult = await openSpecGitRunner(host.workDir, ["rm", "-rf", "--cached", "--ignore-unmatch", sourceRel], host.signal, opts)
  if (!rmResult.success) {
    return archiveFailure("retry-safe", `git rm --cached archive change failed: ${rmResult.combinedOutput || rmResult.stderr || `exit ${rmResult.exitCode}`}`, {
      kind: "archive-change",
      source: changeDir,
      destination,
      stage: "rm",
      rmOutput: rmResult.combinedOutput,
    })
  }

  const diffResult = await openSpecGitRunner(host.workDir, ["diff", "--cached", "--name-only", "--", sourceRel, destinationRel], host.signal, opts)
  if (!diffResult.success) {
    return archiveFailure("retry-safe", `git diff archive change failed: ${diffResult.combinedOutput || diffResult.stderr || `exit ${diffResult.exitCode}`}`, {
      kind: "archive-change",
      source: changeDir,
      destination,
      stage: "diff",
      diffOutput: diffResult.combinedOutput,
    })
  }

  const changedFiles = [...new Set(diffResult.stdout.split(/\r?\n/).map((line) => line.trim()).filter(Boolean))]
  if (changedFiles.length === 0) {
    await archiveFileSystem.remove(checkpointPath)
    return succeed({
        kind: "archive-change",
        source: changeDir,
        destination,
        changed: false,
        noChange: true,
      })
  }

  const commitResult = await openSpecGitRunner(host.workDir, ["commit", "-m", commitMessage, "--", sourceRel, destinationRel], host.signal, opts)
  if (!commitResult.success) {
    return archiveFailure("retry-safe", `git commit archive change failed: ${commitResult.combinedOutput || commitResult.stderr || `exit ${commitResult.exitCode}`}`, {
      kind: "archive-change",
      source: changeDir,
      destination,
      stage: "commit",
      commitOutput: commitResult.combinedOutput,
      changedFiles,
    })
  }

  await archiveFileSystem.remove(checkpointPath)

  const headResult = await openSpecGitRunner(host.workDir, ["rev-parse", "HEAD"], host.signal, opts)
  const commitSha = headResult.success ? headResult.stdout.trim() : null

  return succeed({
      kind: "archive-change",
      source: changeDir,
      destination,
      changed: true,
      noChange: false,
      commitMessage,
      commitSha,
      commitOutput: commitResult.combinedOutput,
      changedFiles,
    })
}

type ArchiveErrorCode = "retry-safe" | "partial-archive" | "missing-source" | "config-error"

function archiveFailure(errorCode: ArchiveErrorCode, message: string, output: Record<string, JsonValue>): ActionResult {
  void output
  return fail(errorCode, message)
}

function mergeTaskWith(
  defaultWith: JsonObject | undefined,
  task: JsonObject,
  taskId: string | undefined,
) {
  const merged: JsonObject = { ...(defaultWith ?? {}) }
  const taskWith = objectInput(task, "with")
  if (taskWith) Object.assign(merged, taskWith)
  if (merged.prompt !== undefined) merged.prompt = injectOpenSpecTaskPromptSelector(merged.prompt, taskId)
  return Object.keys(merged).length === 0 ? null : merged
}

function mergeTaskExpect(task: JsonObject): JsonObject | null {
  const expect = objectInput(task, "expect")
  return expect ?? null
}

function injectOpenSpecTaskPromptSelector(prompt: JsonValue, taskId: string | undefined): JsonValue {
  if (!isObject(prompt)) return prompt
  if (prompt["uses"] !== OPENSPEC_TASK_PROMPT_LOADER_NAME) return prompt
  const existingWith = objectInput(prompt, "with")
  const nextWith: JsonObject = { ...(existingWith ?? {}) }
  if (taskId?.trim()) nextWith["taskId"] = taskId
  return { ...prompt, with: nextWith }
}

interface ArchiveCheckpoint {
  version: number
  workflowRunId: string
  source: string
  destination: string
}

async function resolveArchiveCheckpoint(host: ActionHost, sourceRel: string): Promise<
  | { kind: "ok"; path: string; value: ArchiveCheckpoint | null }
  | { kind: "failure"; result: ActionResult }
> {
  if (!host.checkpoint) return { kind: "failure", result: archiveFailure("config-error", "Archive change requires the workflow-checkpoint capability", { kind: "archive-change" }) }
  const token = await host.checkpoint.token(`archive-change/${sourceRel}`)
  const key = createHash("sha256").update(`${token}\0${sourceRel}`).digest("hex")
  const result = await openSpecGitRunner(host.workDir, ["rev-parse", "--git-path", `mohist/archive-change/${key}.json`], host.signal, sinkOptions(host))
  if (!result.success) return { kind: "failure", result: archiveFailure("config-error", `Unable to resolve archive checkpoint path: ${result.combinedOutput || result.stderr}`, { kind: "archive-change" }) }
  const rawPath = result.stdout.trim()
  if (!rawPath) return { kind: "failure", result: archiveFailure("config-error", "Git returned an empty archive checkpoint path", { kind: "archive-change" }) }
  const path = isAbsolute(rawPath) ? resolve(rawPath) : resolve(host.workDir, rawPath)
  if (!(await archiveFileSystem.exists(path))) return { kind: "ok", path, value: null }
  let parsed: unknown
  try {
    parsed = JSON.parse(await archiveFileSystem.readText(path))
  } catch (err) {
    return { kind: "failure", result: archiveFailure("config-error", `Malformed archive checkpoint: ${err instanceof Error ? err.message : String(err)}`, { kind: "archive-change" }) }
  }
  if (!isArchiveCheckpoint(parsed) || parsed.source !== sourceRel) {
    return { kind: "failure", result: archiveFailure("config-error", "Archive checkpoint does not match this source", { kind: "archive-change" }) }
  }
  return { kind: "ok", path, value: parsed }
}

async function writeArchiveCheckpoint(path: string, content: string): Promise<void> {
  await mkdir(dirname(path), { recursive: true })
  const temporary = `${path}.tmp-${process.pid}-${Math.random().toString(16).slice(2)}`
  try {
    await writeFile(temporary, content, { encoding: "utf8", flag: "wx" })
    await rename(temporary, path)
  } finally {
    await rm(temporary, { force: true }).catch(() => undefined)
  }
}

function isArchiveCheckpoint(value: unknown): value is ArchiveCheckpoint {
  if (!isObject(value)) return false
  return value.version === ARCHIVE_CHECKPOINT_VERSION
    && typeof value.workflowRunId === "string"
    && typeof value.source === "string"
    && typeof value.destination === "string"
}

function validateWorkspaceRelativePath(value: string): string | null {
  if (!value || isAbsolute(value) || value.startsWith("../") || value.includes("\0")) return `Archive source path escapes workspace: ${value}`
  return null
}

function validateCheckpointDestination(workDir: string, archiveDir: string, destinationRel: string): { kind: "ok"; path: string } | { kind: "failure"; message: string } {
  const path = resolve(workDir, destinationRel)
  const archiveRoot = resolve(archiveDir)
  const inside = relative(archiveRoot, path)
  if (!destinationRel || isAbsolute(destinationRel) || destinationRel.includes("\0") || inside === "" || inside.startsWith("..") || isAbsolute(inside)) {
    return { kind: "failure", message: `Archive checkpoint destination escapes archive root: ${destinationRel}` }
  }
  if (relative(workDir, path).replace(/\\/g, "/") !== destinationRel.replace(/\\/g, "/")) {
    return { kind: "failure", message: `Archive checkpoint destination is not canonical: ${destinationRel}` }
  }
  return { kind: "ok", path }
}

function resolveChangeDir(workDir: string, withInput: JsonObject) {
  const changeDir = stringInput(withInput, "changeDir")
  if (!changeDir?.trim()) return undefined
  return resolveActionPath(workDir, changeDir)
}

async function uniqueDestination(archiveDir: string, baseName: string) {
  let destination = resolveArchiveDestination(archiveDir, baseName)
  if (!destination) return null
  if (!(await archiveFileSystem.exists(destination))) return destination
  for (let version = 2; ; version++) {
    destination = resolveArchiveDestination(archiveDir, `${baseName}-v${version}`)
    if (!destination) return null
    if (!(await archiveFileSystem.exists(destination))) return destination
  }
}

function resolveArchiveDestination(archiveDir: string, name: string): string | null {
  const archiveRoot = resolve(archiveDir)
  const destination = resolve(join(archiveRoot, name))
  const relativeDestination = relative(archiveRoot, destination)
  if (!relativeDestination || relativeDestination.startsWith("..") || isAbsolute(relativeDestination)) return null
  return destination
}

async function hasFiles(directory: string): Promise<boolean> {
  try {
    const entries = await readdir(directory, { withFileTypes: true })
    for (const entry of entries) {
      if (entry.isFile()) return true
      if (entry.isDirectory() && await hasFiles(join(directory, entry.name))) return true
    }
    return false
  } catch {
    return false
  }
}

async function isPresentOfKind(path: string, kind: "file" | "directory"): Promise<boolean> {
  if (!exists(path)) return false
  try {
    const stats = await stat(path)
    return kind === "directory" ? stats.isDirectory() : stats.isFile()
  } catch {
    return false
  }
}

function relativePath(workDir: string, path: string) {
  const relativeToWorkDir = relative(workDir, path)
  if (!relativeToWorkDir || relativeToWorkDir.startsWith("..") || isAbsolute(relativeToWorkDir)) return path
  return relativeToWorkDir.replace(/\\/g, "/")
}
