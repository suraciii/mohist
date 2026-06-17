import { dirname, isAbsolute, join, relative, resolve } from "node:path"
import { mkdir, readdir, readFile, rename } from "node:fs/promises"
import { exists, copyDirectory } from "../system/process.js"
import { git as defaultGit } from "./git.js"
import type { ActionContext, ActionResult, JsonObject, JsonValue } from "../core/types.js"
import { isObject, objectInput, stringInput } from "../core/json.js"
import { resolveActionPath } from "./expectations.js"
import { OPENSPEC_TASK_PROMPT_LOADER_NAME } from "./openspec-task-prompt.js"

const SPEC_SYNC_COMMIT_MESSAGE = "Sync OpenSpec specs from change delta"
const ARCHIVE_CHANGE_COMMIT_MESSAGE = "Archive OpenSpec change"

export type OpenSpecGitRunner = (workDir: string, args: string[], signal: AbortSignal) => Promise<{
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

const DEFAULT_OPENSPEC_ITEMS_PATH = "tasks"

export async function openspecTasksAction(context: ActionContext): Promise<ActionResult> {
  const path = resolveActionPath(context, stringInput(context.with, "path"))
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
    return [{ id, title, uses, with: mergedWith ? JSON.stringify(mergedWith) : null }]
  })

  if (!context.serverConnection) return { status: "failure", message: "Server connection not available" }
  await context.serverConnection.addTasks(context.workflowRunId, tasks)

  return { status: "success", message: `Loaded ${tasks.length} tasks`, output: JSON.stringify({ loaded: tasks.length }) }
}

export async function openspecSyncAction(context: ActionContext): Promise<ActionResult> {
  const changeDir = resolveChangeDir(context)
  if (!changeDir) return { status: "failure", message: "OpenSpec sync requires 'changeDir'" }
  const specsDir = join(changeDir, "specs")
  if (!exists(specsDir)) return { status: "failure", message: `OpenSpec specs directory not found: ${specsDir}` }

  const destination = join(context.workDir, "specs")
  await copyDirectory(specsDir, destination)

  const addResult = await openSpecGitRunner(context.workDir, ["add", "specs/"], context.signal)
  if (!addResult.success) {
    return {
      status: "failure",
      message: `git add specs/ failed: ${addResult.combinedOutput || addResult.stderr || `exit ${addResult.exitCode}`}`,
      output: JSON.stringify({
        kind: "openspec-sync",
        source: specsDir,
        destination,
        stage: "add",
        addOutput: addResult.combinedOutput,
      }),
    }
  }

  const diffResult = await openSpecGitRunner(context.workDir, ["diff", "--cached", "--name-only", "--", "specs/"], context.signal)
  if (!diffResult.success) {
    return {
      status: "failure",
      message: `git diff --cached -- specs/ failed: ${diffResult.combinedOutput || diffResult.stderr || `exit ${diffResult.exitCode}`}`,
      output: JSON.stringify({
        kind: "openspec-sync",
        source: specsDir,
        destination,
        stage: "diff",
        diffOutput: diffResult.combinedOutput,
      }),
    }
  }

  const changedFiles = [...new Set(diffResult.stdout.split(/\r?\n/).map((line) => line.trim()).filter(Boolean))]
  if (changedFiles.length === 0) {
    return {
      status: "success",
      message: "OpenSpec specs already up to date; no changes to commit",
      output: JSON.stringify({
        kind: "openspec-sync",
        source: specsDir,
        destination,
        changed: false,
        noChange: true,
      }),
    }
  }

  const commitResult = await openSpecGitRunner(context.workDir, ["commit", "-m", SPEC_SYNC_COMMIT_MESSAGE, "--", "specs/"], context.signal)
  if (!commitResult.success) {
    return {
      status: "failure",
      message: `git commit specs/ failed: ${commitResult.combinedOutput || commitResult.stderr || `exit ${commitResult.exitCode}`}`,
      output: JSON.stringify({
        kind: "openspec-sync",
        source: specsDir,
        destination,
        stage: "commit",
        commitOutput: commitResult.combinedOutput,
        changedFiles,
      }),
    }
  }

  const headResult = await openSpecGitRunner(context.workDir, ["rev-parse", "HEAD"], context.signal)
  const commitSha = headResult.success ? headResult.stdout.trim() : null

  return {
    status: "success",
    message: "OpenSpec specs synced and committed",
    output: JSON.stringify({
      kind: "openspec-sync",
      source: specsDir,
      destination,
      changed: true,
      noChange: false,
      commitMessage: SPEC_SYNC_COMMIT_MESSAGE,
      commitSha,
      commitOutput: commitResult.combinedOutput,
      changedFiles,
    }),
  }
}

export async function archiveChangeAction(context: ActionContext): Promise<ActionResult> {
  const changeDir = resolveChangeDir(context)
  if (!changeDir) return { status: "failure", message: "Archive change requires 'changeDir'" }

  const archiveDir = join(dirname(changeDir), "archive")
  const sourceName = changeDir.split(/[\\/]/).pop() ?? "change"
  const archivePrefix = `${new Date().toISOString().slice(0, 10)}-${sourceName}`
  const existingArchive = await findExistingArchive(archiveDir, archivePrefix)
  const sourceHasFiles = exists(changeDir) && await hasFiles(changeDir)
  if (existingArchive && !sourceHasFiles) {
    return {
      status: "success",
      message: "Change already archived; no changes to commit",
      output: JSON.stringify({
        kind: "archive-change",
        source: changeDir,
        destination: existingArchive,
        changed: false,
        noChange: true,
      }),
    }
  }

  if (!sourceHasFiles) {
    return { status: "failure", message: `Change directory not found: ${changeDir}` }
  }

  await mkdir(archiveDir, { recursive: true })
  const destination = await uniqueDestination(archiveDir, archivePrefix)
  await rename(changeDir, destination)

  const changesPath = relativePath(context.workDir, dirname(changeDir))
  const destinationPath = relativePath(context.workDir, destination)
  const addResult = await openSpecGitRunner(context.workDir, ["add", "-A", changesPath], context.signal)
  if (!addResult.success) {
    return {
      status: "failure",
      message: `git add archive change failed: ${addResult.combinedOutput || addResult.stderr || `exit ${addResult.exitCode}`}`,
      output: JSON.stringify({
        kind: "archive-change",
        source: changeDir,
        destination,
        stage: "add",
        addOutput: addResult.combinedOutput,
      }),
    }
  }

  const diffResult = await openSpecGitRunner(context.workDir, ["diff", "--cached", "--name-only", "--", destinationPath], context.signal)
  if (!diffResult.success) {
    return {
      status: "failure",
      message: `git diff archive change failed: ${diffResult.combinedOutput || diffResult.stderr || `exit ${diffResult.exitCode}`}`,
      output: JSON.stringify({
        kind: "archive-change",
        source: changeDir,
        destination,
        stage: "diff",
        diffOutput: diffResult.combinedOutput,
      }),
    }
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
      }),
    }
  }

  const commitResult = await openSpecGitRunner(context.workDir, ["commit", "-m", ARCHIVE_CHANGE_COMMIT_MESSAGE, "--", changesPath], context.signal)
  if (!commitResult.success) {
    return {
      status: "failure",
      message: `git commit archive change failed: ${commitResult.combinedOutput || commitResult.stderr || `exit ${commitResult.exitCode}`}`,
      output: JSON.stringify({
        kind: "archive-change",
        source: changeDir,
        destination,
        stage: "commit",
        commitOutput: commitResult.combinedOutput,
        changedFiles,
      }),
    }
  }

  const headResult = await openSpecGitRunner(context.workDir, ["rev-parse", "HEAD"], context.signal)
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
      commitMessage: ARCHIVE_CHANGE_COMMIT_MESSAGE,
      commitSha,
      commitOutput: commitResult.combinedOutput,
      changedFiles,
    }),
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
  return resolveActionPath(context, changeDir)
}

async function uniqueDestination(archiveDir: string, baseName: string) {
  let destination = resolve(join(archiveDir, baseName))
  if (!exists(destination)) return destination
  for (let version = 2; ; version++) {
    destination = resolve(join(archiveDir, `${baseName}-v${version}`))
    if (!exists(destination)) return destination
  }
}

async function findExistingArchive(archiveDir: string, baseName: string) {
  let destination = resolve(join(archiveDir, baseName))
  if (exists(destination) && await hasFiles(destination)) return destination
  for (let version = 2; ; version++) {
    destination = resolve(join(archiveDir, `${baseName}-v${version}`))
    if (exists(destination) && await hasFiles(destination)) return destination
    if (version >= 50) return null
  }
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

function relativePath(workDir: string, path: string) {
  const relativeToWorkDir = relative(workDir, path)
  if (!relativeToWorkDir || relativeToWorkDir.startsWith("..") || isAbsolute(relativeToWorkDir)) return path
  return relativeToWorkDir
}
