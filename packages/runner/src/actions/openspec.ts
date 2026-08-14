import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path"
import { mkdir, readdir, readFile, rename, rm, stat } from "node:fs/promises"
import { exists, copyDirectory, deleteDirectory, readText } from "../system/process.js"
import { currentRunnerFileSystem } from "../system/filesystem.js"
import { currentRunnerResources, type RunnerArchiveFileSystem, type RunnerGitRunner } from "../system/filesystem.js"
import { git as defaultGit, type GitOptions } from "./git.js"
import type { ActionResult, AddTaskInput, JsonObject, JsonValue } from "../core/types.js"
import type { ActionHost } from "./host.js"
import { isObject, objectInput, stringInput } from "../core/json.js"
import { resolveActionPath } from "./expectations.js"
import { OPENSPEC_TASK_PROMPT_LOADER_NAME } from "./openspec-task-prompt.js"
import { fail, succeed } from "./action-result.js"

const ARCHIVE_CHANGE_COMMIT_MESSAGE_PREFIX = "Archive OpenSpec change"

const ACTION_SOURCE = "action:openspec"

function sinkOptions(host: ActionHost): GitOptions | undefined {
  return host.log ? { sink: { log: host.log, source: ACTION_SOURCE } } : undefined
}

export type OpenSpecGitRunner = RunnerGitRunner
export type ArchiveFileSystem = RunnerArchiveFileSystem

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
  writeAtomic: async (path, content) => {
    await mkdir(dirname(path), { recursive: true })
    const temporary = `${path}.tmp-${process.pid}-${Math.random().toString(16).slice(2)}`
    try {
      const { writeFile } = await import("node:fs/promises")
      await writeFile(temporary, content, { encoding: "utf8", flag: "wx" })
      await rename(temporary, path)
    } finally {
      await rm(temporary, { force: true }).catch(() => undefined)
    }
  },
  remove: async (path) => await rm(path, { force: true }),
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

  const root = JSON.parse(await readText(path)) as JsonObject
  const sourceTasks = Array.isArray(root.tasks) ? root.tasks.filter(isObject) : []
  if (!Array.isArray(root.tasks)) return fail("invalid-input", "tasks.json must contain a tasks array")

  const taskDefaults = objectInput(inputs, "task")
  const templateUses = taskDefaults?.["uses"]
  if (typeof templateUses !== "string" || !templateUses.trim()) {
    return fail("invalid-input", "OpenSpec task loader requires non-empty 'task.uses'")
  }
  const sourceUses = sourceTasks.find((task) => Object.prototype.hasOwnProperty.call(task, "uses"))
  if (sourceUses) {
    const sourceTaskId = stringInput(sourceUses, "id") ?? stringInput(sourceUses, "taskId") ?? "unknown"
    return fail(
      "invalid-input",
      `tasks.json task '${sourceTaskId}' must not declare 'uses'; configure 'task.uses' on mohist/openspec-tasks`,
    )
  }
  const taskUses = templateUses.trim()
  const defaultWith = objectInput(taskDefaults, "with")
  const itemsPath = stringInput(inputs, "items") ?? DEFAULT_OPENSPEC_ITEMS_PATH
  const buildPrompt = stringInput(inputs, "buildPrompt")
  const tasks: AddTaskInput[] = sourceTasks.flatMap((task) => {
    const id = stringInput(task, "id") ?? stringInput(task, "taskId")
    if (!id?.trim()) return []
    const title = stringInput(task, "title") ?? id
    const mergedWith = mergeTaskWith(defaultWith, task, id, { file: path, items: itemsPath }, buildPrompt)
    const expect = mergeTaskExpect(task)
    return [{ id, title, uses: taskUses, with: mergedWith ?? null, expect }]
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
  const archiveFileSystem = currentRunnerResources()?.archiveFileSystem ?? defaultArchiveFileSystem
  const openSpecGitRunner = currentRunnerResources()?.openSpecGitRunner ?? defaultGit
  const changeDir = resolveChangeDir(host.workDir, inputs)
  if (!changeDir) return archiveFailure("config-error", "Archive change requires 'changeDir'", { kind: "archive-change" })

  const archiveDir = join(dirname(changeDir), "archive")
  const sourceName = basename(changeDir) || "change"
  const sourceRel = relativePath(host.workDir, changeDir)
  const sourceValidation = validateWorkspaceRelativePath(sourceRel)
  if (sourceValidation) return archiveFailure("config-error", sourceValidation, { kind: "archive-change" })

  const hintRel = readArchiveHint(inputs)
  const hintedDestination = hintRel ? validateHintDestination(host.workDir, archiveDir, hintRel) : null
  if (hintRel && !hintedDestination) {
    return archiveFailure("config-error", `Archive hint escapes archive root: ${hintRel}`, { kind: "archive-change" })
  }

  const sourcePresent = await archiveFileSystem.exists(changeDir)
  const destinationPresent = hintedDestination ? await archiveFileSystem.exists(hintedDestination) : false

  if (hintedDestination && destinationPresent && !sourcePresent) {
    // Already archived on a prior run and the source has since been moved;
    // the archived destination is the source of truth. Idempotent success —
    // no move, no commit, no variable rewrite (the var already holds this
    // destination from the prior run).
    return archiveSuccessNoChange(changeDir, hintedDestination, hintRel!, /* writeVar */ false)
  }
  if (sourcePresent && destinationPresent && hintedDestination) {
    return archiveFailure("partial-archive", `Both source and archive exist; refusing to proceed: source=${changeDir} archive=${hintedDestination}`, { kind: "archive-change" })
  }
  if (!sourcePresent) {
    return archiveFailure("missing-source", `Change directory not found: ${changeDir}`, { kind: "archive-change" })
  }
  if (!(await archiveFileSystem.hasFiles(changeDir))) {
    return archiveFailure("missing-source", `Change directory is empty: ${changeDir}`, { kind: "archive-change" })
  }

  await archiveFileSystem.ensureDirectory(archiveDir)
  const today = new Date().toISOString().slice(0, 10)
  const archivePrefix = `${today}-${sourceName}`
  const destination = await uniqueDestination(archiveFileSystem, archiveDir, archivePrefix)
  if (!destination) return archiveFailure("config-error", `Archive destination escapes archive root: ${archivePrefix}`, { kind: "archive-change" })
  const destinationRel = relativePath(host.workDir, destination)
  const destinationValidation = validateHintDestination(host.workDir, archiveDir, destinationRel)
  if (!destinationValidation) return archiveFailure("config-error", `Archive destination escapes archive root: ${destinationRel}`, { kind: "archive-change" })

  try {
    await archiveFileSystem.moveDirectory(changeDir, destination)
  } catch (err) {
    return archiveFailure("retry-safe", `Failed to move change directory: ${err instanceof Error ? err.message : String(err)}`, { kind: "archive-change" })
  }

  const commitMessage = `${ARCHIVE_CHANGE_COMMIT_MESSAGE_PREFIX}: ${sourceName}`
  const opts = sinkOptions(host)

  const addResult = await openSpecGitRunner(host.workDir, ["add", "-A", destinationRel], host.signal, opts)
  if (!addResult.success) {
    await rollbackMove(archiveFileSystem, destination, changeDir)
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
    await rollbackMove(archiveFileSystem, destination, changeDir)
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
    await rollbackMove(archiveFileSystem, destination, changeDir)
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
    // Nothing to commit (e.g. the move was already committed on a prior
    // attempt). The archive is durable; persist the destination so future
    // reruns treat it as idempotent.
    return archiveSuccessNoChange(changeDir, destination, destinationRel, /* writeVar */ true)
  }

  const commitResult = await openSpecGitRunner(host.workDir, ["commit", "-m", commitMessage, "--", sourceRel, destinationRel], host.signal, opts)
  if (!commitResult.success) {
    await rollbackMove(archiveFileSystem, destination, changeDir)
    return archiveFailure("retry-safe", `git commit archive change failed: ${commitResult.combinedOutput || commitResult.stderr || `exit ${commitResult.exitCode}`}`, {
      kind: "archive-change",
      source: changeDir,
      destination,
      stage: "commit",
      commitOutput: commitResult.combinedOutput,
      changedFiles,
    })
  }

  const headResult = await openSpecGitRunner(host.workDir, ["rev-parse", "HEAD"], host.signal, opts)
  const commitSha = headResult.success ? headResult.stdout.trim() : null

  return {
    output: {
      kind: "archive-change" as const,
      source: changeDir,
      destination,
      destinationRel,
      changed: true,
      noChange: false,
      commitMessage,
      commitSha,
      commitOutput: commitResult.combinedOutput,
      changedFiles,
    },
    effects: { writeVars: { archive: destinationRel } },
  }
}

function archiveSuccessNoChange(changeDir: string, destination: string, destinationRel: string, writeVar: boolean): ActionResult {
  const output: JsonObject = {
    kind: "archive-change",
    source: changeDir,
    destination,
    destinationRel,
    changed: false,
    noChange: true,
  }
  return writeVar
    ? { output, effects: { writeVars: { archive: destinationRel } } }
    : { output }
}

async function rollbackMove(fileSystem: ArchiveFileSystem, destination: string, source: string): Promise<void> {
  // If the commit phase fails after the directory move, roll the move back so
  // a retry starts from a clean first-archive state (source present, no
  // partial archive). Best effort — a failed rollback only means the next
  // attempt will see both source and destination and report partial-archive.
  try {
    await fileSystem.moveDirectory(destination, source)
  } catch {
    // Swallow: the caller already failed; the retry surface is the source
    // of truth, not this rollback.
  }
}

function readArchiveHint(inputs: JsonObject): string | null {
  const raw = inputs["archiveHint"]
  if (raw === null || raw === undefined) return null
  if (typeof raw !== "string") return null
  const trimmed = raw.trim()
  return trimmed === "" ? null : trimmed
}

function validateHintDestination(workDir: string, archiveDir: string, destinationRel: string): string | null {
  const path = resolve(workDir, destinationRel)
  const archiveRoot = resolve(archiveDir)
  const inside = relative(archiveRoot, path)
  if (!destinationRel || isAbsolute(destinationRel) || destinationRel.includes("\0") || inside === "" || inside.startsWith("..") || isAbsolute(inside)) {
    return null
  }
  if (relative(workDir, path).replace(/\\/g, "/") !== destinationRel.replace(/\\/g, "/")) {
    return null
  }
  return path
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
  loaderConfig: { file: string; items: string },
  buildPrompt: string | undefined,
) {
  const merged: JsonObject = { ...(defaultWith ?? {}) }
  const taskWith = objectInput(task, "with")
  if (taskWith) Object.assign(merged, taskWith)
  if (merged.prompt === undefined) {
    merged.prompt = buildOpenSpecTaskPromptSpec(taskId, loaderConfig, buildPrompt)
  } else {
    merged.prompt = injectOpenSpecTaskPromptSelector(merged.prompt, taskId)
  }
  return Object.keys(merged).length === 0 ? null : merged
}

function buildOpenSpecTaskPromptSpec(
  taskId: string | undefined,
  loaderConfig: { file: string; items: string },
  buildPrompt: string | undefined,
): JsonObject {
  const loaderWith: JsonObject = {
    file: loaderConfig.file,
    items: loaderConfig.items,
  }
  if (buildPrompt && buildPrompt.trim().length > 0) loaderWith["base"] = buildPrompt
  if (taskId?.trim()) loaderWith["taskId"] = taskId
  return {
    uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
    with: loaderWith,
  }
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

function validateWorkspaceRelativePath(value: string): string | null {
  if (!value || isAbsolute(value) || value.startsWith("../") || value.includes("\0")) return `Archive source path escapes workspace: ${value}`
  return null
}

function resolveChangeDir(workDir: string, withInput: JsonObject) {
  const changeDir = stringInput(withInput, "changeDir")
  if (!changeDir?.trim()) return undefined
  return resolveActionPath(workDir, changeDir)
}

async function uniqueDestination(fileSystem: ArchiveFileSystem, archiveDir: string, baseName: string) {
  let destination = resolveArchiveDestination(archiveDir, baseName)
  if (!destination) return null
  if (!(await fileSystem.exists(destination))) return destination
  for (let version = 2; ; version++) {
    destination = resolveArchiveDestination(archiveDir, `${baseName}-v${version}`)
    if (!destination) return null
    if (!(await fileSystem.exists(destination))) return destination
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
    const stats = await currentRunnerFileSystem().stat(path)
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
