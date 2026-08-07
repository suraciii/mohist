import { existsSync } from "node:fs"
import { basename, resolve } from "node:path"
import type { CleanupEntry, CleanupRunner } from "./cleanup-loop.js"
import type { AgentWorkspaceRegistry, AgentWorkspaceRegistryEntry } from "./agent-workspace-registry.js"
import {
  CHILD_SESSION_ID_PATTERN,
  agentWorktreeBranch,
  agentWorkspacePath,
  validateAgentWorktree,
  type AgentWorkspaceOwnershipDeps,
} from "./agent-workspace.js"
import { isUnderRunnerRoot } from "./workspace-query.js"
import { deleteDirectory, runCommand } from "../system/process.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("cleanup")

// Cleanup-runner implementation for agent managed worktrees. Deletion
// must go through `git worktree remove` (clears the working tree AND
// the parent repo's admin entry) + `git worktree prune` — never
// `rm -rf`, which would leave a stale admin entry behind. The disk
// identity probe reads the worktree `.git` backing, standing in for
// the workflow marker check of the workflow cleanup runner.
export class AgentCleanupRunner implements CleanupRunner {
  constructor(
    private readonly runnerRoot: string,
    private readonly registry: AgentWorkspaceRegistry,
    private readonly deps: AgentWorkspaceOwnershipDeps = { registry },
  ) {}

  isUnderRunnerRoot(root: string, candidate: string): boolean {
    return isUnderRunnerRoot(root, candidate)
  }

  pathExists(path: string): boolean {
    return existsSync(path)
  }

  async readWorkspaceIdentity(workspacePath: string): Promise<string | null> {
    const name = basename(workspacePath)
    if (!CHILD_SESSION_ID_PATTERN.test(name)) return null
    const verdict = await validateAgentWorktree(this.runnerRoot, workspacePath, name, this.deps, new AbortController().signal)
    return verdict.ok ? name : null
  }

  async deleteDirectory(path: string): Promise<void> {
    await deleteDirectory(path)
  }

  async computeDirectorySize(path: string, signal: AbortSignal): Promise<number | null> {
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

  // A worktree with an `active` child worktree pointing at it must not
  // be removed: the child shares its object store.
  async hasActiveDependents(entry: CleanupEntry): Promise<boolean> {
    const target = resolve(entry.workspacePath)
    return this.registry.list().some((candidate) => candidate.phase === "active" && resolve(candidate.parentWorkDir) === target)
  }

  async validateWorkspace(entry: CleanupEntry): Promise<boolean> {
    const agent = entry as AgentWorkspaceRegistryEntry
    if (!this.isTrackedAgentEntry(agent)) return false
    const verdict = await validateAgentWorktree(this.runnerRoot, agent.workspacePath, agent.childSessionId, this.deps, new AbortController().signal)
    return verdict.ok
  }

  async validateAndDeleteWorkspace(entry: CleanupEntry): Promise<boolean> {
    const agent = entry as AgentWorkspaceRegistryEntry
    if (!this.isTrackedAgentEntry(agent)) return false
    const signal = new AbortController().signal
    const verdict = await validateAgentWorktree(this.runnerRoot, agent.workspacePath, agent.childSessionId, this.deps, signal)
    if (!verdict.ok) return false

    const remove = await runCommand("git", ["-C", agent.parentWorkDir, "worktree", "remove", "--force", agent.workspacePath], ".", signal)
    if (remove.exitCode !== 0) {
      log.warn("agent worktree removal refused", { session: agent.childSessionId, path: agent.workspacePath, reason: remove.stderr || remove.stdout })
      return false
    }
    // Best-effort hygiene: `git worktree remove` clears the admin entry
    // but leaves the branch ref; `prune` clears stale admin entries.
    await runCommand("git", ["-C", agent.parentWorkDir, "branch", "-D", agent.branch], ".", signal).catch(() => undefined)
    const prune = await runCommand("git", ["-C", agent.parentWorkDir, "worktree", "prune"], ".", signal)
    if (prune.exitCode !== 0) {
      log.warn("agent worktree prune failed", { session: agent.childSessionId, parent: agent.parentWorkDir, reason: prune.stderr || prune.stdout })
    }
    return true
  }

  private isTrackedAgentEntry(entry: AgentWorkspaceRegistryEntry): boolean {
    if (!CHILD_SESSION_ID_PATTERN.test(entry.childSessionId)) return false
    if (entry.branch !== agentWorktreeBranch(entry.childSessionId)) return false
    if (entry.workspacePath !== agentWorkspacePath(this.runnerRoot, entry.childSessionId)) return false
    return true
  }
}
