import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path"
import { mkdir, readdir, readFile, rename, stat } from "node:fs/promises"
import { exists, copyDirectory, deleteDirectory } from "../system/process.js"
import { git as defaultGit, type GitOptions } from "./git.js"
import type { ActionContext, ActionResult, JsonObject, JsonValue } from "../core/types.js"
import { isObject, objectInput, stringInput } from "../core/json.js"
import { resolveActionPath } from "./expectations.js"
import { OPENSPEC_TASK_PROMPT_LOADER_NAME } from "./openspec-task-prompt.js"

const ARCHIVE_CHANGE_COMMIT_MESSAGE_PREFIX = "Archive OpenSpec change"
const OPENSPEC_ARCHIVE_NAME_VAR_KEY = "openspecArchiveName"
const ARCHIVE_DESTINATION_VAR_KEY = "_actions.archiveChange.destination"

/**
 * `source` tag recorded against every captured `mohist/openspec`
 * action body line. Phase-distinguished from `branch-check` and
 * `cleanup` so the web viewer can tell which ops phase produced
 * which line.
 */
const ACTION_SOURCE = "action:openspec"

function sinkOptions(context: ActionContext): GitOptions | undefined {
  return context.log ? { sink: { log: context.log, source: ACTION_SOURCE } } : undefined
}

function readLegacyArchiveName(variables: JsonObject, sourceRel: string): string | null {
  const raw = variables[ARCHIVE_DESTINATION_VAR_KEY]
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return null
  const entry = (raw as JsonObject)[sourceRel]
  return typeof entry === "string" && entry.length > 0 ? entry : null
}

function resolveEffectiveArchiveName(
  variables: JsonObject,
  sourceRel: string,
  sourceName: string,
  today: string,
): { name: string; persistedSource: "new" | "legacy" | null } {
  const newName = variables[OPENSPEC_ARCHIVE_NAME_VAR_KEY]
  if (typeof newName === "string" && newName.length > 0) {
    return { name: newName, persistedSource: "new" }
  }
  const legacyName = readLegacyArchiveName(variables, sourceRel)
  if (legacyName) {
    return { name: legacyName, persistedSource: "legacy" }
  }
  return { name: `${today}-${sourceName}`, persistedSource: null }
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

export async function openspecTasksAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context.workDir, stringInput(context.with, "path"))
  if (!path) return { status: "failure", message: "OpenSpec task loader requires 'path'" }
  if (!exists(path)) return { status: "failure", message: `tasks.json not found: ${path}` }

  const root = JSON.parse(await readFile(path, "utf8")) as JsonObject
  const sourceTasks = Array.isArray(root.tasks) ? root.tasks.filter(isObject) : []
  if (!Array.isArray(root.tasks)) return { status: "failure", message: "tasks.json must contain a tasks array" }

  const taskDefaults = objectInput(context.with, "task")
  const defaultUses = stringInput(taskDefaults, "uses") ?? "mohist/acp-agent"
  const defaultWith = objectInput(taskDefaults, "with")
  const itemsPath = stringInput(context.with, "items") ?? DEFAULT_OPENSPEC_ITEMS_PATH
  const tasks = sourceTasks.flatMap((task) => {
    const id = stringInput(task, "id") ?? stringInput(task, "taskId")
    if (!id?.trim()) return []
    const title = stringInput(task, "title") ?? id
    const uses = stringInput(task, "uses") ?? defaultUses
    const mergedWith = mergeTaskWith(defaultWith, task, id, { file: path, items: itemsPath }, context.variables)
    return [{ id, title, uses, with: mergedWith ?? null }]
  })

  if (!context.serverConnection) return { status: "failure", message: "Server connection not available" }
  await context.serverConnection.addTasks(context.workflowRunId, tasks)

  return { status: "success", message: `Loaded ${tasks.length} tasks`, output: JSON.stringify({ loaded: tasks.length }) }
}

export async function openspecArtifactsAction(context: ActionContext): Promise<ActionResult> {
  const changeDir = resolveChangeDir(context)
  if (!changeDir) return { status: "failure", message: "OpenSpec artifacts check requires 'changeDir'" }

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
  const output = JSON.stringify({
    kind: "openspec-artifacts",
    changeDir,
    present,
    missing,
  })

  if (present) {
    return {
      status: "success",
      message: `OpenSpec artifacts present under ${changeDir}`,
      output,
    }
  }

  return {
    status: "failure",
    message: `OpenSpec artifacts missing under ${changeDir}: ${missing.join(", ")}`,
    output,
  }
}

export async function archiveChangeAction(context: ActionContext): Promise<ActionResult> {
  const changeDir = resolveChangeDir(context)
  if (!changeDir) return archiveFailure("config-error", "Archive change requires 'changeDir'", { kind: "archive-change" })

  const archiveDir = join(dirname(changeDir), "archive")
  const sourceName = basename(changeDir) || "change"
  const sourceRel = relativePath(context.workDir, changeDir)
  const today = new Date().toISOString().slice(0, 10)

  const { name: archivePrefix, persistedSource } = resolveEffectiveArchiveName(
    context.variables,
    sourceRel,
    sourceName,
    today,
  )
  const invalidArchivePrefix = validateArchivePrefix(archivePrefix)
  if (invalidArchivePrefix) {
    return archiveFailure("config-error", `Invalid archive name for ${sourceName}: ${invalidArchivePrefix}`, {
      kind: "archive-change",
      source: changeDir,
      archivePrefix,
      stage: "validate-archive-name",
    })
  }

  const sourceHasFiles = exists(changeDir) && (await hasFiles(changeDir))

  let destination: string
  if (persistedSource) {
    const persistedDestination = resolveArchiveDestination(archiveDir, archivePrefix)
    if (!persistedDestination) {
      return archiveFailure("config-error", `Archive destination escapes archive root: ${archivePrefix}`, {
        kind: "archive-change",
        source: changeDir,
        archivePrefix,
        stage: "resolve-destination",
      })
    }
    const persistedArchiveHasFiles = exists(persistedDestination) && (await hasFiles(persistedDestination))
    if (persistedArchiveHasFiles && sourceHasFiles) {
      return archiveFailure(
        "partial-archive",
        `Both source and archive exist; refusing to proceed: source=${changeDir} archive=${persistedDestination}`,
        { kind: "archive-change", source: changeDir, archive: persistedDestination },
      )
    }
    if (persistedArchiveHasFiles) {
      if (persistedSource === "legacy") {
        const persistFailure = await persistArchiveName(context, archivePrefix, changeDir)
        if (persistFailure) return persistFailure
      }
      destination = persistedDestination
    } else if (!sourceHasFiles) {
      return archiveFailure(
        "missing-source",
        `Change directory not found: ${changeDir}`,
        { kind: "archive-change", source: changeDir },
      )
    } else {
      await mkdir(archiveDir, { recursive: true })
      if (exists(persistedDestination)) {
        return archiveFailure(
          "partial-archive",
          `Archive destination already exists; refusing to overwrite: source=${changeDir} archive=${persistedDestination}`,
          { kind: "archive-change", source: changeDir, archive: persistedDestination },
        )
      }
      const persistFailure = await persistArchiveName(context, archivePrefix, changeDir)
      if (persistFailure) return persistFailure
      destination = persistedDestination
      try {
        await moveChangeDir(changeDir, destination)
      } catch (err) {
        return archiveFailure("retry-safe", `Failed to move change directory: ${err instanceof Error ? err.message : String(err)}`, {
          kind: "archive-change",
          source: changeDir,
          destination,
          stage: "rename",
        })
      }
    }
  } else if (!sourceHasFiles) {
    const existingArchive = await findExistingArchive(archiveDir, archivePrefix)
    if (existingArchive) {
      const backfilledName = basename(existingArchive)
      const invalidBackfilled = validateArchivePrefix(backfilledName)
      if (invalidBackfilled) {
        return archiveFailure("config-error", `Invalid archive name for ${sourceName}: ${invalidBackfilled}`, {
          kind: "archive-change",
          source: changeDir,
          archivePrefix: backfilledName,
          stage: "validate-archive-name",
        })
      }
      const backfillFailure = await persistArchiveName(context, backfilledName, changeDir)
      if (backfillFailure) return backfillFailure
      destination = existingArchive
    } else {
      return archiveFailure(
        "missing-source",
        `Change directory not found: ${changeDir}`,
        { kind: "archive-change", source: changeDir },
      )
    }
  } else {
    await mkdir(archiveDir, { recursive: true })
    const resolvedDestination = await uniqueDestination(archiveDir, archivePrefix)
    if (!resolvedDestination) {
      return archiveFailure("config-error", `Archive destination escapes archive root: ${archivePrefix}`, {
        kind: "archive-change",
        source: changeDir,
        archivePrefix,
        stage: "resolve-destination",
      })
    }
    const resolvedArchiveName = basename(resolvedDestination)
    const persistFailure = await persistArchiveName(context, resolvedArchiveName, changeDir)
    if (persistFailure) return persistFailure
    destination = resolvedDestination
    try {
      await moveChangeDir(changeDir, destination)
    } catch (err) {
      return archiveFailure("retry-safe", `Failed to move change directory: ${err instanceof Error ? err.message : String(err)}`, {
        kind: "archive-change",
        source: changeDir,
        destination,
        stage: "rename",
      })
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
    return {
      status: "success",
      message: "Change already archived; no changes to commit",
      output: JSON.stringify({
        kind: "archive-change",
        source: changeDir,
        destination,
        changed: false,
        noChange: true,
        errorCode: null,
      }),
    }
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

  const headResult = await openSpecGitRunner(context.workDir, ["rev-parse", "HEAD"], context.signal, opts)
  const commitSha = headResult.success ? headResult.stdout.trim() : null

  return {
    status: "success",
    message: "Change archived and committed",
    output: JSON.stringify({
      kind: "archive-change",
      source: changeDir,
      destination,
      changed: true,
      noChange: false,
      commitMessage,
      commitSha,
      commitOutput: commitResult.combinedOutput,
      changedFiles,
      errorCode: null,
    }),
  }
}

type ArchiveErrorCode = "retry-safe" | "partial-archive" | "missing-source" | "config-error"

function archiveFailure(errorCode: ArchiveErrorCode, message: string, output: Record<string, JsonValue>): ActionResult {
  return { status: "failure", message, output: JSON.stringify({ ...output, errorCode }) }
}

async function persistArchiveName(
  context: ActionContext,
  archiveName: string,
  source: string,
): Promise<ActionResult | null> {
  try {
    await context.writeVars({ [OPENSPEC_ARCHIVE_NAME_VAR_KEY]: archiveName })
    return null
  } catch (err) {
    return archiveFailure(
      "retry-safe",
      `Failed to persist archive name: ${err instanceof Error ? err.message : String(err)}`,
      {
        kind: "archive-change",
        source,
        archivePrefix: archiveName,
        stage: "persist-name",
      },
    )
  }
}

function mergeTaskWith(
  defaultWith: JsonObject | undefined,
  task: JsonObject,
  taskId: string | undefined,
  loaderConfig: { file: string; items: string },
  variables?: JsonObject,
) {
  const merged: JsonObject = { ...(defaultWith ?? {}) }
  const taskWith = objectInput(task, "with")
  if (taskWith) Object.assign(merged, taskWith)
  if (merged.prompt === undefined) {
    merged.prompt = buildOpenSpecTaskPromptSpec(taskId, loaderConfig, variables)
  } else {
    merged.prompt = injectOpenSpecTaskPromptSelector(merged.prompt, taskId)
  }
  return Object.keys(merged).length === 0 ? null : merged
}

function injectOpenSpecTaskPromptSelector(prompt: JsonValue, taskId: string | undefined): JsonValue {
  if (!isObject(prompt)) return prompt
  if (prompt["uses"] !== OPENSPEC_TASK_PROMPT_LOADER_NAME) return prompt
  const existingWith = objectInput(prompt, "with")
  const nextWith: JsonObject = { ...(existingWith ?? {}) }
  if (taskId?.trim()) nextWith["taskId"] = taskId
  return { ...prompt, with: nextWith }
}

function buildOpenSpecTaskPromptSpec(
  taskId: string | undefined,
  loaderConfig: { file: string; items: string },
  variables?: JsonObject,
): JsonObject {
  const loaderWith: JsonObject = {
    file: loaderConfig.file,
    items: loaderConfig.items,
  }
  const base = resolveBuildPrompt(variables)
  if (base !== undefined) loaderWith["base"] = base
  if (taskId?.trim()) loaderWith["taskId"] = taskId
  return {
    uses: OPENSPEC_TASK_PROMPT_LOADER_NAME,
    with: loaderWith,
  }
}

function resolveBuildPrompt(variables?: JsonObject): string | undefined {
  if (!variables) return undefined
  const prompts = variables["prompts"]
  if (typeof prompts !== "object" || prompts === null || Array.isArray(prompts)) return undefined
  const build = (prompts as JsonObject)["build"]
  return typeof build === "string" ? build : undefined
}

function resolveChangeDir(context: ActionContext) {
  const changeDir = stringInput(context.with, "changeDir")
  if (!changeDir?.trim()) return undefined
  return resolveActionPath(context.workDir, changeDir)
}

function validateArchivePrefix(prefix: string): string | null {
  if (!prefix) return "must not be empty"
  if (prefix.trim() !== prefix) return "must not contain leading or trailing whitespace"
  if (prefix === "." || prefix === "..") return "must not be a relative path segment"
  if (prefix.includes("/") || prefix.includes("\\") || prefix.includes("\0")) return "must be a single path segment"
  if (isAbsolute(prefix) || /^[A-Za-z]:/.test(prefix)) return "must not be an absolute path"
  return null
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

async function findExistingArchive(archiveDir: string, baseName: string) {
  let destination = resolveArchiveDestination(archiveDir, baseName)
  if (!destination) return null
  if (exists(destination) && await hasFiles(destination)) return destination
  for (let version = 2; ; version++) {
    destination = resolveArchiveDestination(archiveDir, `${baseName}-v${version}`)
    if (!destination) return null
    if (exists(destination) && await hasFiles(destination)) return destination
    if (version >= 50) return null
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
