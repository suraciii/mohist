import { createHash } from "node:crypto"
import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path"
import { mkdir, readdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises"
import { exists, copyDirectory, deleteDirectory } from "../system/process.js"
import { git as defaultGit, type GitOptions } from "./git.js"
import type { ActionResult, JsonObject, JsonValue } from "../core/types.js"
import type { ActionInvocationContext } from "./context.js"
import { isObject, objectInput, stringInput } from "../core/json.js"
import { resolveActionPath } from "./expectations.js"
import { OPENSPEC_TASK_PROMPT_LOADER_NAME } from "./openspec-task-prompt.js"
import { fail, succeed } from "./action-result.js"

const ARCHIVE_CHANGE_COMMIT_MESSAGE_PREFIX = "Archive OpenSpec change"
const ARCHIVE_CHECKPOINT_VERSION = 1

/**
 * `source` tag recorded against every captured `mohist/openspec`
 * action body line. Phase-distinguished from `branch-check` and
 * `cleanup` so the web viewer can tell which ops phase produced
 * which line.
 */
const ACTION_SOURCE = "action:openspec"

function sinkOptions(context: ActionInvocationContext): GitOptions | undefined {
  return context.log ? { sink: { log: context.log, source: ACTION_SOURCE } } : undefined
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

type ArchiveRename = (src: string, dst: string) => Promise<void>

let archiveRenameForTest: ArchiveRename | null = null

export function setArchiveRenameForTest(rename: ArchiveRename | null) {
  archiveRenameForTest = rename
}

async function moveChangeDir(src: string, dst: string) {
  try {
    if (archiveRenameForTest) {
      await archiveRenameForTest(src, dst)
    } else {
      await rename(src, dst)
    }
  } catch (err) {
    if (isCrossDeviceError(err)) {
      await copyDirectory(src, dst)
      await deleteDirectory(src)
      return
    }
    throw err
  }
}

function isCrossDeviceError(err: unknown): boolean {
  return Boolean(err && typeof err === "object" && (err as { code?: string }).code === "EXDEV")
}

const DEFAULT_OPENSPEC_ITEMS_PATH = "tasks"

export async function openspecTasksAction(context: ActionInvocationContext): Promise<ActionResult> {
  const path = resolveActionPath(context.workDir, stringInput(context.with, "path"))
  if (!path) return fail("invalid-input", "OpenSpec task loader requires 'path'")
  if (!exists(path)) return fail("missing-source", `tasks.json not found: ${path}`)

  const root = JSON.parse(await readFile(path, "utf8")) as JsonObject
  const sourceTasks = Array.isArray(root.tasks) ? root.tasks.filter(isObject) : []
  if (!Array.isArray(root.tasks)) return fail("invalid-input", "tasks.json must contain a tasks array")

  const taskDefaults = objectInput(context.rawWith ?? context.with, "task")
  const defaultUses = stringInput(taskDefaults, "uses") ?? "mohist/opencode"
  const defaultWith = objectInput(taskDefaults, "with")
  const itemsPath = stringInput(context.with, "items") ?? DEFAULT_OPENSPEC_ITEMS_PATH
  const tasks = sourceTasks.flatMap((task) => {
    const id = stringInput(task, "id") ?? stringInput(task, "taskId")
    if (!id?.trim()) return []
    const title = stringInput(task, "title") ?? id
    const uses = stringInput(task, "uses") ?? defaultUses
    const mergedWith = mergeTaskWith(defaultWith, task, id)
    const expect = mergeTaskExpect(task)
    return [{ id, title, uses, with: mergedWith ?? null, expect }]
  })

  if (!context.serverConnection) return fail("server-unavailable", "Server connection not available")
  await context.serverConnection.addTasks(context.workflowRunId, tasks)

  return succeed({ loaded: tasks.length })
}

export async function openspecArtifactsAction(context: ActionInvocationContext): Promise<ActionResult> {
  const changeDir = resolveChangeDir(context)
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

export async function archiveChangeAction(context: ActionInvocationContext): Promise<ActionResult> {
  const changeDir = resolveChangeDir(context)
  if (!changeDir) return archiveFailure("config-error", "Archive change requires 'changeDir'", { kind: "archive-change" })

  const archiveDir = join(dirname(changeDir), "archive")
  const sourceName = basename(changeDir) || "change"
  const sourceRel = relativePath(context.workDir, changeDir)
  const sourceValidation = validateWorkspaceRelativePath(sourceRel)
  if (sourceValidation) return archiveFailure("config-error", sourceValidation, { kind: "archive-change" })

  const checkpoint = await resolveArchiveCheckpoint(context, sourceRel)
  if (checkpoint.kind === "failure") return checkpoint.result

  let destination: string
  const checkpointPath = checkpoint.path
  if (checkpoint.value) {
    const persisted = checkpoint.value
    const destinationValidation = validateCheckpointDestination(context.workDir, archiveDir, persisted.destination)
    if (destinationValidation.kind === "failure") return archiveFailure("config-error", destinationValidation.message, { kind: "archive-change" })
    destination = destinationValidation.path
    const sourcePresent = exists(changeDir)
    const destinationPresent = exists(destination)
    if (sourcePresent && destinationPresent) {
      return archiveFailure("partial-archive", `Both source and archive exist; refusing to proceed: source=${changeDir} archive=${destination}`, { kind: "archive-change" })
    }
    if (!sourcePresent && !destinationPresent) {
      return archiveFailure("missing-source", `Change directory not found: ${changeDir}`, { kind: "archive-change" })
    }
    if (sourcePresent) {
      try {
        await moveChangeDir(changeDir, destination)
      } catch (err) {
        return archiveFailure("retry-safe", `Failed to move change directory: ${err instanceof Error ? err.message : String(err)}`, { kind: "archive-change" })
      }
    }
  } else {
    if (!(exists(changeDir) && await hasFiles(changeDir))) {
      return archiveFailure("missing-source", `Change directory not found: ${changeDir}`, { kind: "archive-change" })
    }
    await mkdir(archiveDir, { recursive: true })
    const today = new Date().toISOString().slice(0, 10)
    const archivePrefix = `${today}-${sourceName}`
    const resolvedDestination = await uniqueDestination(archiveDir, archivePrefix)
    if (!resolvedDestination) return archiveFailure("config-error", `Archive destination escapes archive root: ${archivePrefix}`, { kind: "archive-change" })
    destination = resolvedDestination
    const destinationRel = relativePath(context.workDir, destination)
    const destinationValidation = validateCheckpointDestination(context.workDir, archiveDir, destinationRel)
    if (destinationValidation.kind === "failure") return archiveFailure("config-error", destinationValidation.message, { kind: "archive-change" })
    const checkpointValue: ArchiveCheckpoint = {
      version: ARCHIVE_CHECKPOINT_VERSION,
      workflowRunId: context.workflowRunId,
      source: sourceRel,
      destination: destinationRel,
    }
    try {
      await writeArchiveCheckpoint(checkpointPath, checkpointValue)
    } catch (err) {
      return archiveFailure("retry-safe", `Failed to persist archive checkpoint: ${err instanceof Error ? err.message : String(err)}`, { kind: "archive-change" })
    }
    try {
      await moveChangeDir(changeDir, destination)
    } catch (err) {
      return archiveFailure("retry-safe", `Failed to move change directory: ${err instanceof Error ? err.message : String(err)}`, { kind: "archive-change" })
    }
  }

  const destinationRel = relativePath(context.workDir, destination)
  const commitMessage = `${ARCHIVE_CHANGE_COMMIT_MESSAGE_PREFIX}: ${sourceName}`
  const opts = sinkOptions(context)

  const addResult = await openSpecGitRunner(context.workDir, ["add", "-A", destinationRel], context.signal, opts)
  if (!addResult.success) {
    return archiveFailure("retry-safe", `git add archive change failed: ${addResult.combinedOutput || addResult.stderr || `exit ${addResult.exitCode}`}`, {
      kind: "archive-change",
      source: changeDir,
      destination,
      stage: "add",
      addOutput: addResult.combinedOutput,
    })
  }

  const rmResult = await openSpecGitRunner(context.workDir, ["rm", "-rf", "--cached", "--ignore-unmatch", sourceRel], context.signal, opts)
  if (!rmResult.success) {
    return archiveFailure("retry-safe", `git rm --cached archive change failed: ${rmResult.combinedOutput || rmResult.stderr || `exit ${rmResult.exitCode}`}`, {
      kind: "archive-change",
      source: changeDir,
      destination,
      stage: "rm",
      rmOutput: rmResult.combinedOutput,
    })
  }

  const diffResult = await openSpecGitRunner(context.workDir, ["diff", "--cached", "--name-only", "--", sourceRel, destinationRel], context.signal, opts)
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
    await clearArchiveCheckpoint(checkpointPath)
    return succeed({
        kind: "archive-change",
        source: changeDir,
        destination,
        changed: false,
        noChange: true,
      })
  }

  const commitResult = await openSpecGitRunner(context.workDir, ["commit", "-m", commitMessage, "--", sourceRel, destinationRel], context.signal, opts)
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

  await clearArchiveCheckpoint(checkpointPath)

  const headResult = await openSpecGitRunner(context.workDir, ["rev-parse", "HEAD"], context.signal, opts)
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

/**
 * Propagate the task-level `expect` declaration from the OpenSpec
 * task template into the generated AddTaskInput. The executor's
 * completion evaluator owns the contract; the loader must NOT
 * swallow `expect`. A missing `expect` becomes `null` (the executor
 * then skips completion evaluation).
 */
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

async function resolveArchiveCheckpoint(context: ActionInvocationContext, sourceRel: string): Promise<
  | { kind: "ok"; path: string; value: ArchiveCheckpoint | null }
  | { kind: "failure"; result: ActionResult }
> {
  const key = createHash("sha256").update(`${context.workflowRunId}\0${sourceRel}`).digest("hex")
  const result = await openSpecGitRunner(context.workDir, ["rev-parse", "--git-path", `mohist/archive-change/${key}.json`], context.signal, sinkOptions(context))
  if (!result.success) return { kind: "failure", result: archiveFailure("config-error", `Unable to resolve archive checkpoint path: ${result.combinedOutput || result.stderr}`, { kind: "archive-change" }) }
  const rawPath = result.stdout.trim()
  if (!rawPath) return { kind: "failure", result: archiveFailure("config-error", "Git returned an empty archive checkpoint path", { kind: "archive-change" }) }
  const path = isAbsolute(rawPath) ? resolve(rawPath) : resolve(context.workDir, rawPath)
  if (!exists(path)) return { kind: "ok", path, value: null }
  let parsed: unknown
  try {
    parsed = JSON.parse(await readFile(path, "utf8"))
  } catch (err) {
    return { kind: "failure", result: archiveFailure("config-error", `Malformed archive checkpoint: ${err instanceof Error ? err.message : String(err)}`, { kind: "archive-change" }) }
  }
  if (!isArchiveCheckpoint(parsed) || parsed.workflowRunId !== context.workflowRunId || parsed.source !== sourceRel) {
    return { kind: "failure", result: archiveFailure("config-error", "Archive checkpoint does not match this workflow run and source", { kind: "archive-change" }) }
  }
  return { kind: "ok", path, value: parsed }
}

async function writeArchiveCheckpoint(path: string, value: ArchiveCheckpoint): Promise<void> {
  await mkdir(dirname(path), { recursive: true })
  const temporary = `${path}.tmp-${process.pid}-${Math.random().toString(16).slice(2)}`
  try {
    await writeFile(temporary, `${JSON.stringify(value)}\n`, { encoding: "utf8", flag: "wx" })
    await rename(temporary, path)
  } finally {
    await rm(temporary, { force: true }).catch(() => undefined)
  }
}

async function clearArchiveCheckpoint(path: string): Promise<void> {
  await rm(path, { force: true })
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

function resolveChangeDir(context: ActionInvocationContext) {
  const changeDir = stringInput(context.with, "changeDir")
  if (!changeDir?.trim()) return undefined
  return resolveActionPath(context.workDir, changeDir)
}

async function uniqueDestination(archiveDir: string, baseName: string) {
  let destination = resolveArchiveDestination(archiveDir, baseName)
  if (!destination) return null
  if (!exists(destination)) return destination
  for (let version = 2; ; version++) {
    destination = resolveArchiveDestination(archiveDir, `${baseName}-v${version}`)
    if (!destination) return null
    if (!exists(destination)) return destination
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
