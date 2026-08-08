import { createHash } from "node:crypto"
import { join, resolve } from "node:path"
import { deleteDirectory, ensureDir, exists, readText, runCommand } from "../system/process.js"
import { currentRunnerFileSystem } from "../system/filesystem.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../actions/git.js"
import type { ServerConnection } from "../server/connection.js"
import type { NamedWorkspaceRegistry } from "./workspace-registry.js"
import { slugify, withManagedWorkspacePath } from "./workspace.js"

// Named workspace (Workspace entity) materialization. The named
// workspace is a PERSISTENT EMPTY-OR-ACCUMULATED directory — it is
// deliberately NOT a clone/run-branch workspace: no git organization at
// all. The agent self-organizes (clone under `repos/`, work products at
// the workspace root, per the prompt convention). Materialization only
// guarantees the directory exists, is not a symlink (the managed-path
// walk), and carries an identity marker so cleanup guards can match
// disk reality to the runner-local registry.

export interface NamedWorkspaceRepository {
  name: string
  gitUrl: string
}

export interface NamedWorkspaceMarker {
  projectId: string
  workspaceName: string
  repositories: NamedWorkspaceRepository[]
}

export interface NamedWorkspaceMaterializeResult {
  path: string
  // True when this call created the directory (it did not exist before).
  // The caller uses this to decide whether yielding to a claimed home
  // runner may delete the directory without destroying pre-existing
  // work.
  created: boolean
}

const MARKER_RELATIVE_PATH = ".mohist/workspace.json"

// Deterministic per-(projectId, workspaceName) directory under the
// managed workspace parent. The slug disambiguates the common case and
// a short content hash keeps rare slug collisions (e.g. "a b" vs "a-b")
// from mapping two distinct workspaces onto one directory.
export function namedWorkspacePath(runnerRoot: string, projectId: string, workspaceName: string): string {
  const digest = createHash("sha256").update(`${projectId}/${workspaceName}`).digest("hex").slice(0, 8)
  const component = `${slugify(projectId)}-${slugify(workspaceName)}-${digest}`
  return resolve(join(runnerRoot, "workspaces", component))
}

export function namedWorkspaceMarkerPath(workspacePath: string): string {
  return join(workspacePath, MARKER_RELATIVE_PATH)
}

export async function readNamedWorkspaceMarker(workspacePath: string): Promise<NamedWorkspaceMarker | null> {
  const path = namedWorkspaceMarkerPath(workspacePath)
  if (!exists(path)) return null
  try {
    const raw = await readText(path)
    const parsed = JSON.parse(raw) as Partial<NamedWorkspaceMarker>
    if (typeof parsed.projectId !== "string" || typeof parsed.workspaceName !== "string") return null
    const repositories = Array.isArray(parsed.repositories)
      ? parsed.repositories.filter(
          (r): r is NamedWorkspaceRepository =>
            typeof r === "object" && r !== null
            && typeof (r as NamedWorkspaceRepository).name === "string"
            && typeof (r as NamedWorkspaceRepository).gitUrl === "string",
        )
      : []
    return { projectId: parsed.projectId, workspaceName: parsed.workspaceName, repositories }
  } catch {
    return null
  }
}

export interface NamedWorkspaceMaterializeOptions {
  runnerRoot: string
  projectId: string
  workspaceName: string
  repositories?: readonly NamedWorkspaceRepository[]
  registry: NamedWorkspaceRegistry
  now?: () => Date
}

// Ensure the named workspace's persistent directory exists and record
// the identity marker + registry entry. Re-materialization (directory
// recycled by cleanup) is just ensureDir again: the platform does not
// promise continuity, so there is nothing to restore — the next agent
// starts from the empty directory.
export async function materializeNamedWorkspace(
  options: NamedWorkspaceMaterializeOptions,
): Promise<NamedWorkspaceMaterializeResult> {
  const workspacePath = namedWorkspacePath(options.runnerRoot, options.projectId, options.workspaceName)
  let created = false
  await withManagedWorkspacePath(options.runnerRoot, workspacePath, false, async (managedWorkspacePath) => {
    if (!exists(managedWorkspacePath)) {
      await currentRunnerFileSystem().ensureDir(managedWorkspacePath)
      created = true
    }
    const markerDir = join(managedWorkspacePath, ".mohist")
    await currentRunnerFileSystem().ensureDir(markerDir)
    const marker: NamedWorkspaceMarker = {
      projectId: options.projectId,
      workspaceName: options.workspaceName,
      repositories: options.repositories ? [...options.repositories] : [],
    }
    await currentRunnerFileSystem().writeText(join(managedWorkspacePath, MARKER_RELATIVE_PATH), JSON.stringify(marker, null, 2))
  })
  await options.registry.register({
    projectId: options.projectId,
    workspaceName: options.workspaceName,
    workspacePath,
  })
  return { path: workspacePath, created }
}

export interface IssueWorkspaceCloneOptions {
  runnerRoot: string
  projectId: string
  workspaceName: string
  gitUrl: string
  baseBranch: string
  runBranch: string
  registry: NamedWorkspaceRegistry
  signal: AbortSignal
  now?: () => Date
}

export async function materializeIssueWorkspace(
  options: IssueWorkspaceCloneOptions,
): Promise<NamedWorkspaceMaterializeResult> {
  const workspacePath = namedWorkspacePath(options.runnerRoot, options.projectId, options.workspaceName)
  let created = false
  await withManagedWorkspacePath(options.runnerRoot, workspacePath, false, async (managedWorkspacePath) => {
    if (exists(managedWorkspacePath)) {
      const marker = await readNamedWorkspaceMarker(managedWorkspacePath)
      if (!marker || marker.projectId !== options.projectId || marker.workspaceName !== options.workspaceName) {
        throw new Error(`Named workspace marker mismatch at ${managedWorkspacePath}`)
      }
      return
    }

    const preparationPath = `${managedWorkspacePath}.preparing`
    if (exists(preparationPath)) await deleteDirectory(preparationPath)
    await ensureDir(join(preparationPath, ".."))

    const cloneResult = await runCommand("git", ["clone", options.gitUrl, preparationPath], ".", options.signal, undefined, { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS })
    if (cloneResult.exitCode !== 0) {
      await deleteDirectory(preparationPath).catch(() => {})
      throw new Error(`git clone failed for ${options.gitUrl}: ${cloneResult.stderr || cloneResult.stdout}`)
    }

    const originResult = await runCommand("git", ["-C", preparationPath, "remote", "get-url", "origin"], ".", options.signal)
    if (originResult.exitCode !== 0 || originResult.stdout.trim() !== options.gitUrl.trim()) {
      await deleteDirectory(preparationPath).catch(() => {})
      throw new Error(`Workspace clone origin mismatch: expected ${options.gitUrl}, got ${originResult.stdout.trim()}`)
    }

    const hasRemote = await runCommand("git", ["-C", preparationPath, "rev-parse", "--verify", `refs/remotes/origin/${options.runBranch}`], ".", options.signal)
    if (hasRemote.exitCode === 0) {
      const checkoutResult = await runCommand("git", ["-C", preparationPath, "checkout", "-B", options.runBranch, `origin/${options.runBranch}`], ".", options.signal)
      if (checkoutResult.exitCode !== 0) {
        await deleteDirectory(preparationPath).catch(() => {})
        throw new Error(`Failed to checkout run branch ${options.runBranch}: ${checkoutResult.stderr || checkoutResult.stdout}`)
      }
    } else {
      const branchResult = await runCommand("git", ["-C", preparationPath, "checkout", "-B", options.runBranch, `origin/${options.baseBranch}`], ".", options.signal)
      if (branchResult.exitCode !== 0) {
        await deleteDirectory(preparationPath).catch(() => {})
        throw new Error(`Failed to create run branch ${options.runBranch} from ${options.baseBranch}: ${branchResult.stderr || branchResult.stdout}`)
      }
    }

    const markerDir = join(preparationPath, ".mohist")
    await currentRunnerFileSystem().ensureDir(markerDir)
    const marker: NamedWorkspaceMarker = {
      projectId: options.projectId,
      workspaceName: options.workspaceName,
      repositories: [{ name: options.gitUrl.split("/").pop()?.replace(".git", "") ?? "unknown", gitUrl: options.gitUrl }],
    }
    await currentRunnerFileSystem().writeText(join(preparationPath, MARKER_RELATIVE_PATH), JSON.stringify(marker, null, 2))
    await currentRunnerFileSystem().rename(preparationPath, managedWorkspacePath)
    created = true
  })
  await options.registry.register({
    projectId: options.projectId,
    workspaceName: options.workspaceName,
    workspacePath,
  })
  return { path: workspacePath, created }
}

// Thrown when the server refused the materialization report because
// another runner already owns the workspace home (first writer wins).
export class WorkspaceHomeClaimedError extends Error {
  readonly kind = "workspace-home-claimed"
  constructor(message: string) {
    super(message)
    this.name = "WorkspaceHomeClaimedError"
  }
}

// Owns the materialize -> report -> registry lifecycle for a named
// workspace. The server report is the home claim: 409
// `workspace_home_claimed` means another runner won the race, this
// runner yields (deleting only a directory it created this attempt) and
// the dispatch fails; job retry then routes to the home runner via
// admission affinity.
export class NamedWorkspaceManager {
  constructor(
    private readonly runnerRoot: string,
    private readonly registry: NamedWorkspaceRegistry,
    private readonly connection: ServerConnection,
    private readonly now: () => Date = () => new Date(),
  ) {}

  async materializeForIssue(
    projectId: string,
    workspaceName: string,
    gitUrl: string,
    baseBranch: string,
    signal: AbortSignal,
  ): Promise<NamedWorkspaceMaterializeResult> {
    const runBranch = `mohist/ws-${workspaceName}`
    const result = await materializeIssueWorkspace({
      runnerRoot: this.runnerRoot,
      projectId,
      workspaceName,
      gitUrl,
      baseBranch,
      runBranch,
      registry: this.registry,
      signal,
      now: this.now,
    })
    try {
      await this.connection.reportWorkspaceMaterialized(projectId, workspaceName, result.path, signal)
    } catch (error) {
      if (error instanceof WorkspaceHomeClaimedError) {
        if (result.created) {
          await deleteDirectory(result.path).catch(() => {})
          await this.registry.remove(`ws:${projectId}:${workspaceName}`).catch(() => {})
        }
        throw error
      }
      throw error
    }
    return result
  }

  async materialize(
    projectId: string,
    workspaceName: string,
    repositories: readonly NamedWorkspaceRepository[],
    signal: AbortSignal,
  ): Promise<NamedWorkspaceMaterializeResult> {
    const result = await materializeNamedWorkspace({
      runnerRoot: this.runnerRoot,
      projectId,
      workspaceName,
      repositories,
      registry: this.registry,
      now: this.now,
    })
    try {
      await this.connection.reportWorkspaceMaterialized(projectId, workspaceName, result.path, signal)
    } catch (error) {
      if (error instanceof WorkspaceHomeClaimedError) {
        if (result.created) {
          await deleteDirectory(result.path).catch(() => {})
          await this.registry.remove(`ws:${projectId}:${workspaceName}`).catch(() => {})
        }
        throw error
      }
      throw error
    }
    return result
  }
}
