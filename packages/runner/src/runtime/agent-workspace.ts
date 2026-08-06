import { basename, dirname, join, resolve } from "node:path"
import { readdir } from "node:fs/promises"
import type { WorkspaceRegistry } from "./workspace-registry.js"
import type { AgentWorkspaceRegistry, AgentWorkspaceRegisterInput } from "./agent-workspace-registry.js"
import { ensureDir, exists, readText, runCommand } from "../system/process.js"
import { assertManagedWorkspacePath } from "./workspace.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("workspace")

// Agent managed worktree (agent-workspace.md): a child AgentSession's
// isolated working tree, materialized by the pinned Runner as a git
// worktree of the parent workspace's repository. The `ChildSessionId`
// is the single idempotency key: every local coordinate (path, branch,
// worktree name, opaque identity) derives deterministically from it, so
// a lost registry can be rebuilt from safe-ID directories + git
// metadata without markers.

export const CHILD_SESSION_ID_PATTERN = /^[0-9a-f]{32}$/
export const AGENT_WORKSPACES_DIR = "agent-workspaces"

export function agentWorkspacePath(runnerRoot: string, childSessionId: string): string {
  assertChildSessionId(childSessionId)
  return resolve(join(runnerRoot, AGENT_WORKSPACES_DIR, childSessionId))
}

export function agentWorktreeBranch(childSessionId: string): string {
  assertChildSessionId(childSessionId)
  return `mohist/wt-${childSessionId}`
}

export function agentWorktreeName(childSessionId: string): string {
  assertChildSessionId(childSessionId)
  return childSessionId
}

export function agentWorkspaceIdentity(childSessionId: string): string {
  assertChildSessionId(childSessionId)
  return `agent-wt:${childSessionId}`
}

function assertChildSessionId(childSessionId: string): void {
  if (!CHILD_SESSION_ID_PATTERN.test(childSessionId)) {
    throw new Error(`Invalid child session id ${JSON.stringify(childSessionId)}; expected 32 lowercase hex characters`)
  }
}

export interface RepositorySnapshot {
  name: string
  gitUrl: string
  baseBranch: string
}

export type MaterializeRejectionReason =
  | "capacity"
  | "permission"
  | "parent-workspace-unavailable"
  | "repository-mismatch"
  | "invalid"

export type MaterializeAgentWorkspaceResult =
  | { kind: "materialized"; workspaceIdentity: string; workDir: string }
  | { kind: "rejected"; reason: MaterializeRejectionReason; message: string }

export type ReleaseAgentWorkspaceResult =
  | { kind: "released" }
  | { kind: "not-found" }
  | { kind: "invalid"; message: string }

export interface MaterializeAgentWorkspaceRequest {
  projectId: string | null
  childSessionId: string
  parentWorkDir: string
  repository: RepositorySnapshot | null
}

export interface ReleaseAgentWorkspaceRequest {
  childSessionId: string
  workspaceIdentity: string
}

export interface AgentWorkspaceRecoverResult {
  scanned: number
  adopted: number
  skipped: number
}

// Shared ownership facts for the "runner-owned workspace" checks:
// the parent of an agent worktree (and the backing parent of an
// adopted one) must be a workspace THIS runner materialized — a
// registered workflow workspace, a registered agent worktree, or an
// explicitly configured default workspace under the runner root.
export interface AgentWorkspaceOwnershipDeps {
  registry: AgentWorkspaceRegistry
  workflowRegistry?: WorkspaceRegistry | null
  defaultWorkspacePaths?: readonly string[]
}

export async function isRunnerOwnedWorkspacePath(path: string, deps: AgentWorkspaceOwnershipDeps): Promise<boolean> {
  const target = resolve(path)
  try {
    if (deps.workflowRegistry?.findByWorkspacePath(target)) return true
  } catch {
    // Registry not loaded → fail closed: treat as not owned.
  }
  try {
    if (deps.registry.findByWorkspacePath(target)) return true
  } catch {
    // Registry not loaded → fail closed: treat as not owned.
  }
  return (deps.defaultWorkspacePaths ?? []).some((candidate) => resolve(candidate) === target)
}

export type WorktreeValidation =
  | { ok: true; parentWorkDir: string }
  | { ok: false; message: string }

// Read the `gitdir:` line of a linked worktree's `.git` file. Returns
// null when the path has no readable worktree `.git` file (a regular
// `.git` directory or a missing file both qualify).
export async function readWorktreeGitDir(workspacePath: string): Promise<string | null> {
  const gitFile = join(workspacePath, ".git")
  if (!exists(gitFile)) return null
  const raw = await readText(gitFile).catch(() => null)
  if (!raw) return null
  const match = /^gitdir:\s*(.+)$/m.exec(raw)
  return match ? match[1]!.trim() : null
}

// Parse a worktree admin path `<parent>/.git/worktrees/<name>`; the
// entry name must equal the deterministic worktree name.
export function parseWorktreeGitDir(gitDirRaw: string, expectedName: string): { parentWorkDir: string } | null {
  const gitDir = resolve(gitDirRaw)
  if (basename(gitDir) !== expectedName) return null
  if (basename(dirname(gitDir)) !== "worktrees") return null
  const repoGitDir = dirname(dirname(gitDir))
  if (basename(repoGitDir) !== ".git") return null
  return { parentWorkDir: dirname(repoGitDir) }
}

// Fail-closed on-disk validation of an agent worktree, shared by
// materialize adoption, startup recover, and the cleanup runner:
//   - containment under `<runnerRoot>/agent-workspaces/`, no symlink;
//   - `<path>/.git` is a worktree file whose backing entry name equals
//     the deterministic worktree name;
//   - current branch equals the deterministic branch;
//   - the backing parent resolves to a workspace this runner owns.
// Any failure yields `{ ok: false }` and never modifies the directory.
export async function validateAgentWorktree(
  runnerRoot: string,
  workspacePath: string,
  childSessionId: string,
  deps: AgentWorkspaceOwnershipDeps,
  signal: AbortSignal,
): Promise<WorktreeValidation> {
  try {
    await assertManagedWorkspacePath(runnerRoot, workspacePath, true)
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : String(error) }
  }
  const gitDirRaw = await readWorktreeGitDir(workspacePath)
  if (!gitDirRaw) return { ok: false, message: `agent worktree ${workspacePath} has no readable worktree .git file` }
  const parsed = parseWorktreeGitDir(gitDirRaw, agentWorktreeName(childSessionId))
  if (!parsed) return { ok: false, message: `agent worktree ${workspacePath} backing entry does not match ${agentWorktreeName(childSessionId)}` }
  const branch = await runCommand("git", ["-C", workspacePath, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
  if (branch.exitCode !== 0 || branch.stdout.trim() !== agentWorktreeBranch(childSessionId)) {
    return { ok: false, message: `agent worktree ${workspacePath} is not on branch ${agentWorktreeBranch(childSessionId)}` }
  }
  if (!(await isRunnerOwnedWorkspacePath(parsed.parentWorkDir, deps))) {
    return { ok: false, message: `agent worktree ${workspacePath} backing parent ${parsed.parentWorkDir} is not a runner-owned workspace` }
  }
  return { ok: true, parentWorkDir: parsed.parentWorkDir }
}

export interface AgentWorkspaceManagerOptions extends AgentWorkspaceOwnershipDeps {
  // Current shared workspace storage budget (bytes) from the latest
  // server cleanup policy; `null`/`<= 0` disables the capacity check.
  getStorageBudgetBytes?: () => number | null
  // Injectable size probe (tests). Defaults to `du -sb` on the runner
  // root, matching the cleanup loop's shared-budget measurement.
  computeDirectorySize?: (path: string, signal: AbortSignal) => Promise<number | null>
}

export class AgentWorkspaceManager {
  private readonly registry: AgentWorkspaceRegistry
  private readonly options: {
    workflowRegistry: WorkspaceRegistry | null
    defaultWorkspacePaths: readonly string[]
    getStorageBudgetBytes: () => number | null
    computeDirectorySize: (path: string, signal: AbortSignal) => Promise<number | null>
  }
  private readonly inFlight = new Map<string, Promise<MaterializeAgentWorkspaceResult>>()

  constructor(
    private readonly runnerRoot: string,
    options: AgentWorkspaceManagerOptions,
  ) {
    this.registry = options.registry
    this.options = {
      workflowRegistry: options.workflowRegistry ?? null,
      defaultWorkspacePaths: options.defaultWorkspacePaths ?? [],
      getStorageBudgetBytes: options.getStorageBudgetBytes ?? (() => null),
      computeDirectorySize: options.computeDirectorySize ?? defaultComputeDirectorySize,
    }
  }

  // Materialize (or replay) the worktree for a ChildSessionId. The
  // registry entry is the idempotency record: a request for an already
  // registered key returns the recorded (identity, workDir) without
  // touching disk or creating a second worktree.
  async materialize(request: MaterializeAgentWorkspaceRequest, signal: AbortSignal): Promise<MaterializeAgentWorkspaceResult> {
    const key = request.childSessionId
    const existing = this.inFlight.get(key)
    if (existing) return existing
    const operation = this.performMaterialize(request, signal)
    this.inFlight.set(key, operation)
    void operation.finally(() => {
      if (this.inFlight.get(key) === operation) this.inFlight.delete(key)
    })
    return operation
  }

  private async performMaterialize(request: MaterializeAgentWorkspaceRequest, signal: AbortSignal): Promise<MaterializeAgentWorkspaceResult> {
    const { childSessionId, parentWorkDir, repository } = request
    if (!CHILD_SESSION_ID_PATTERN.test(childSessionId)) {
      return rejected("invalid", `childSessionId must match ${CHILD_SESSION_ID_PATTERN.source}`)
    }
    if (!repository || !repository.name || !repository.gitUrl || !repository.baseBranch) {
      return rejected("invalid", "repository snapshot is required")
    }
    const branch = agentWorktreeBranch(childSessionId)
    const worktreePath = agentWorkspacePath(this.runnerRoot, childSessionId)

    const existing = this.registry.get(childSessionId)
    if (existing) {
      return { kind: "materialized", workspaceIdentity: existing.workspaceIdentity, workDir: existing.workspacePath }
    }

    const parent = await this.validateParentWorkDir(parentWorkDir, repository.gitUrl, signal)
    if (parent.kind === "failed") {
      return rejected(parent.reason === "origin-mismatch" ? "repository-mismatch" : "parent-workspace-unavailable", parent.message)
    }

    if (!(await this.hasStorageHeadroom(signal))) {
      return rejected("capacity", "runner workspace storage budget is exhausted")
    }

    if (exists(worktreePath)) {
      const verdict = await validateAgentWorktree(this.runnerRoot, worktreePath, childSessionId, this.ownershipDeps(), signal)
      if (!verdict.ok) return rejected("invalid", verdict.message)
      await this.register({ request, branch, worktreePath, parentWorkDir: verdict.parentWorkDir })
      return { kind: "materialized", workspaceIdentity: agentWorkspaceIdentity(childSessionId), workDir: worktreePath }
    }

    const created = await this.createWorktree(worktreePath, branch, parent.path, signal)
    if (created.kind === "failed") return created.result
    await this.register({ request, branch, worktreePath, parentWorkDir: parent.path })
    return { kind: "materialized", workspaceIdentity: agentWorkspaceIdentity(childSessionId), workDir: worktreePath }
  }

  // Release an agent worktree. The identity pair (ChildSessionId +
  // WorkspaceIdentity) must match, else the request is rejected. The
  // entry is marked eligible (Server is authoritative on release); disk
  // removal happens in the maintenance cycle under the removal fence.
  async release(request: ReleaseAgentWorkspaceRequest): Promise<ReleaseAgentWorkspaceResult> {
    if (!CHILD_SESSION_ID_PATTERN.test(request.childSessionId)) {
      return { kind: "invalid", message: `childSessionId must match ${CHILD_SESSION_ID_PATTERN.source}` }
    }
    if (request.workspaceIdentity !== agentWorkspaceIdentity(request.childSessionId)) {
      return { kind: "invalid", message: "workspaceIdentity does not match childSessionId" }
    }
    const existing = this.registry.get(request.childSessionId)
    if (!existing) return { kind: "not-found" }
    await this.registry.markEligible(request.childSessionId)
    return { kind: "released" }
  }

  // Rescan `<runnerRoot>/agent-workspaces/` and re-register every
  // directory whose name is a safe child session id and whose git
  // metadata validates. Existing registry entries keep their phase;
  // unknown directories start `active`. Invalid directories are
  // skipped (never treated as active parents for deletion decisions).


  // Runner-owned + origin validation of a parent workdir, shared with
  // the WorkspaceSourceConfirmer. Fail-closed: any containment /
  // symlink / ownership / `.git` failure is `not-runner-owned`; an
  // unreadable or unequal origin is `origin-mismatch`.
  async validateParentWorkDir(
    workDir: string,
    expectedGitUrl: string,
    signal: AbortSignal,
  ): Promise<{ kind: "ok"; path: string } | { kind: "failed"; reason: "not-runner-owned" | "origin-mismatch"; message: string }> {
    let parentPath: string
    try {
      await assertManagedWorkspacePath(this.runnerRoot, workDir, true)
      parentPath = resolve(workDir)
    } catch (error) {
      return { kind: "failed", reason: "not-runner-owned", message: error instanceof Error ? error.message : String(error) }
    }
    if (!(await isRunnerOwnedWorkspacePath(parentPath, this.ownershipDeps()))) {
      return { kind: "failed", reason: "not-runner-owned", message: `parent workspace ${parentPath} is not a workspace this runner owns` }
    }
    if (!exists(join(parentPath, ".git"))) {
      return { kind: "failed", reason: "not-runner-owned", message: `parent workspace ${parentPath} has no readable .git` }
    }
    const origin = await runCommand("git", ["-C", parentPath, "remote", "get-url", "origin"], ".", signal)
    if (origin.exitCode !== 0 || origin.stdout.trim() !== expectedGitUrl.trim()) {
      return { kind: "failed", reason: "origin-mismatch", message: `parent workspace ${parentPath} origin does not match repository gitUrl` }
    }
    return { kind: "ok", path: parentPath }
  }

  async isRunnerOwnedWorkspace(path: string): Promise<boolean> {
    return isRunnerOwnedWorkspacePath(path, this.ownershipDeps())
  }

  private async register(input: {
    request: Pick<MaterializeAgentWorkspaceRequest, "projectId" | "childSessionId" | "repository">
    branch: string
    worktreePath: string
    parentWorkDir: string
  }): Promise<void> {
    const record: AgentWorkspaceRegisterInput = {
      childSessionId: input.request.childSessionId,
      projectId: input.request.projectId,
      workspaceIdentity: agentWorkspaceIdentity(input.request.childSessionId),
      workspacePath: input.worktreePath,
      branch: input.branch,
      parentWorkDir: input.parentWorkDir,
      repositoryName: input.request.repository?.name ?? null,
    }
    await this.registry.register(record)
  }

  private async createWorktree(
    worktreePath: string,
    branch: string,
    parentPath: string,
    signal: AbortSignal,
  ): Promise<{ kind: "ok" } | { kind: "failed"; result: MaterializeAgentWorkspaceResult }> {
    try {
      await assertManagedWorkspacePath(this.runnerRoot, worktreePath, false)
    } catch (error) {
      return { kind: "failed", result: rejected("invalid", error instanceof Error ? error.message : String(error)) }
    }
    await ensureDir(join(this.runnerRoot, AGENT_WORKSPACES_DIR))
    const head = await runCommand("git", ["-C", parentPath, "rev-parse", "HEAD"], ".", signal)
    if (head.exitCode !== 0) {
      return { kind: "failed", result: rejected("parent-workspace-unavailable", `parent HEAD is unreadable: ${head.stderr || head.stdout}`) }
    }
    // The worktree path is recorded verbatim in the parent repo's
    // admin entries, so it must be the real path (a /proc fd alias
    // would go stale the moment the handle closes and break prune).
    const add = await runCommand("git", ["-C", parentPath, "worktree", "add", "-B", branch, worktreePath, head.stdout.trim()], ".", signal)
    if (add.exitCode !== 0) {
      return { kind: "failed", result: rejected("permission", `git worktree add failed: ${add.stderr || add.stdout}`) }
    }
    return { kind: "ok" }
  }

  private async hasStorageHeadroom(signal: AbortSignal): Promise<boolean> {
    const budget = this.options.getStorageBudgetBytes()
    if (budget == null || budget <= 0) return true
    if (!exists(this.runnerRoot)) return true
    const usage = await this.options.computeDirectorySize(this.runnerRoot, signal)
    if (usage == null) return false
    return usage < budget
  }

  // Rescan `<runnerRoot>/agent-workspaces/` and re-register every
  // directory whose name is a safe child session id and whose git
  // metadata validates (containment, no symlink, `.git` backing, branch,
  // backing parent owned). The backing parent is derived from the
  // worktree's `gitdir` entry — always the main workspace whose object
  // store the worktree shares. Existing registry entries keep their
  // phase; unknown directories start `active`. Invalid directories are
  // skipped (never treated as active parents for deletion decisions).
  async recover(signal: AbortSignal): Promise<AgentWorkspaceRecoverResult> {
    const agentRoot = join(this.runnerRoot, AGENT_WORKSPACES_DIR)
    if (!exists(agentRoot)) return { scanned: 0, adopted: 0, skipped: 0 }
    let entries
    try {
      entries = await readdir(agentRoot, { withFileTypes: true })
    } catch (error) {
      log.error("agent workspace recover scan failed", { root: agentRoot, exception: error })
      return { scanned: 0, adopted: 0, skipped: 0 }
    }
    let scanned = 0
    let adopted = 0
    let skipped = 0
    for (const entry of entries) {
      if (signal.aborted) break
      if (!entry.isDirectory() || !CHILD_SESSION_ID_PATTERN.test(entry.name)) continue
      scanned += 1
      const workspacePath = join(agentRoot, entry.name)
      const verdict = await validateAgentWorktree(this.runnerRoot, workspacePath, entry.name, this.ownershipDeps(), signal)
      if (!verdict.ok) {
        skipped += 1
        continue
      }
      if (!this.registry.get(entry.name)) {
        await this.register({
          request: { projectId: null, childSessionId: entry.name, repository: null },
          branch: agentWorktreeBranch(entry.name),
          worktreePath: workspacePath,
          parentWorkDir: verdict.parentWorkDir,
        })
      }
      adopted += 1
    }
    return { scanned, adopted, skipped }
  }

  private ownershipDeps(): AgentWorkspaceOwnershipDeps {
    return {
      registry: this.registry,
      workflowRegistry: this.options.workflowRegistry,
      defaultWorkspacePaths: this.options.defaultWorkspacePaths,
    }
  }
}

function rejected(reason: MaterializeRejectionReason, message: string): { kind: "rejected"; reason: MaterializeRejectionReason; message: string } {
  return { kind: "rejected", reason, message }
}

async function defaultComputeDirectorySize(path: string, signal: AbortSignal): Promise<number | null> {
  try {
    const result = await runCommand("du", ["-sb", path], ".", signal)
    if (result.exitCode !== 0) return null
    const match = result.stdout.match(/^(\d+)/)
    if (!match) return null
    return parseInt(match[1], 10)
  } catch {
    return null
  }
}
