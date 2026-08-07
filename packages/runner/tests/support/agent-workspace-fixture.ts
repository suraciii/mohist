import { mkdir, readFile, rm, symlink, writeFile } from "node:fs/promises"
import { existsSync } from "node:fs"
import { basename, dirname, join } from "node:path"
import type { CommandLineOptions, CommandResult } from "../../src/system/process.js"
import type { WorkspaceRegistry } from "../../src/runtime/workspace-registry.js"
import type { AgentWorkspaceRegistry } from "../../src/runtime/agent-workspace-registry.js"
import { AgentWorkspaceManager, type MaterializeAgentWorkspaceResult } from "../../src/runtime/agent-workspace.js"

export interface CommandCall {
  command: string
  args: string[]
  cwd: string
}

// Fake git runner modelling a parent repo + linked worktrees on a real
// temp directory: `worktree add` writes the worktree `.git` file and
// the parent's admin entry (as real git does), so the manager's
// on-disk validation reads real artifacts.
export class FakeAgentGit {
  readonly calls: CommandCall[] = []
  readonly origins = new Map<string, string>()
  readonly heads = new Map<string, string>()
  readonly branches = new Map<string, string>()
  readonly sizes = new Map<string, number>()
  readonly worktrees = new Map<string, { commonGitDir: string; branch: string }>()
  failNextWorktreeAdd = false
  failNextWorktreeRemove = false
  defaultHead = "fake-head-sha"
  defaultBranch = "master"

  commandArgs(): string[][] {
    return this.calls.map((call) => call.args)
  }

  async run(
    command: string,
    args: string[],
    cwd: string,
    _signal: AbortSignal,
    _env?: NodeJS.ProcessEnv,
    _options?: CommandLineOptions,
  ): Promise<CommandResult> {
    this.calls.push({ command, args: [...args], cwd })
    if (command === "du") {
      const path = args[1]
      const size = this.sizes.get(path) ?? 1000
      return commandResult(0, `${size}\t${path}\n`)
    }
    if (command !== "git") throw new Error(`Unexpected command: ${command}`)
    if (args[0] !== "-C" || !args[1]) throw new Error(`Unexpected git arguments: ${args.join(" ")}`)
    const repo = args[1]
    const gitArgs = args.slice(2)

    if (gitArgs[0] === "remote" && gitArgs[1] === "get-url" && gitArgs[2] === "origin") {
      const origin = this.origins.get(repo)
      return origin === undefined
        ? commandResult(1, "", "fatal: no such remote 'origin'")
        : commandResult(0, `${origin}\n`)
    }

    if (gitArgs[0] === "rev-parse" && gitArgs[1] === "HEAD") {
      return commandResult(0, `${this.heads.get(repo) ?? this.defaultHead}\n`)
    }

    if (gitArgs[0] === "rev-parse" && gitArgs[1] === "--abbrev-ref" && gitArgs[2] === "HEAD") {
      return commandResult(0, `${this.branches.get(repo) ?? this.defaultBranch}\n`)
    }

    if (gitArgs[0] === "worktree" && gitArgs[1] === "add") {
      if (this.failNextWorktreeAdd) {
        this.failNextWorktreeAdd = false
        return commandResult(1, "", "fake worktree add failure")
      }
      const branch = gitArgs[3]
      const path = gitArgs[4]
      if (!branch || !path) throw new Error(`Unexpected worktree add arguments: ${gitArgs.join(" ")}`)
      const name = basename(path)
      const commonGitDir = await this.commonGitDir(repo)
      await mkdir(join(commonGitDir, "worktrees", name), { recursive: true })
      await writeFile(join(commonGitDir, "worktrees", name, "gitdir"), `${path}\n`)
      await mkdir(path, { recursive: true })
      await writeFile(join(path, ".git"), `gitdir: ${join(commonGitDir, "worktrees", name)}\n`)
      this.branches.set(path, branch)
      this.worktrees.set(path, { commonGitDir, branch })
      return commandResult(0)
    }

    if (gitArgs[0] === "worktree" && gitArgs[1] === "remove") {
      if (this.failNextWorktreeRemove) {
        this.failNextWorktreeRemove = false
        return commandResult(1, "", "fake worktree remove failure")
      }
      const path = gitArgs[3]
      const meta = this.worktrees.get(path)
      if (!meta) return commandResult(1, "", "fatal: not a working tree")
      await rm(path, { recursive: true, force: true })
      await rm(join(meta.commonGitDir, "worktrees", basename(path)), { recursive: true, force: true })
      this.worktrees.delete(path)
      this.branches.delete(path)
      return commandResult(0)
    }

    if (gitArgs[0] === "worktree" && gitArgs[1] === "prune") {
      for (const [path, meta] of [...this.worktrees]) {
        if (!existsSync(path)) {
          await rm(join(meta.commonGitDir, "worktrees", basename(path)), { recursive: true, force: true })
          this.worktrees.delete(path)
        }
      }
      return commandResult(0)
    }

    if (gitArgs[0] === "branch" && gitArgs[1] === "-D") {
      return commandResult(0)
    }

    throw new Error(`Unexpected git arguments: ${args.join(" ")}`)
  }

  // Resolve the shared git directory of a repo path: a main worktree
  // has `<repo>/.git` as a directory; a linked worktree has it as a
  // file whose `gitdir:` line points into `<commonDir>/worktrees/<name>`.
  private async commonGitDir(repo: string): Promise<string> {
    const gitPath = join(repo, ".git")
    if (existsSync(join(gitPath, "worktrees"))) return gitPath
    const raw = await readFile(gitPath, "utf8").catch(() => null)
    if (!raw) return gitPath
    const match = /^gitdir:\s*(.+)$/m.exec(raw)
    if (!match) return gitPath
    return match[1]!.trim().replace(/[\\/]worktrees[\\/][^\\/]+$/, "")
  }
}

export function commandResult(exitCode = 0, stdout = "", stderr = ""): CommandResult {
  return { exitCode, stdout, stderr }
}

export function expectMaterialized(result: MaterializeAgentWorkspaceResult) {
  if (result.kind !== "materialized") throw new Error(`materialize failed: ${result.reason}`)
  return result
}

// Create a runner-owned workflow workspace at
// `<root>/workspaces/<workflowRunId>` (registered + .git + origin).
export async function createRunnerOwnedParent(
  root: string,
  workflowRegistry: WorkspaceRegistry,
  fake: FakeAgentGit,
  workflowRunId = "wr-parent-1",
  gitUrl = "https://example.test/mohist.git",
): Promise<string> {
  const parentPath = join(root, "workspaces", workflowRunId)
  await mkdir(join(parentPath, ".git"), { recursive: true })
  await workflowRegistry.register({ issueNumber: 1, workflowRunId, workspacePath: parentPath, runBranch: `mohist/run-${workflowRunId}` })
  fake.origins.set(parentPath, gitUrl)
  return parentPath
}

// Create a manager wired to the fake's size probe.
export function createAgentManager(
  root: string,
  registry: AgentWorkspaceRegistry,
  fake: FakeAgentGit,
  options: {
    workflowRegistry?: WorkspaceRegistry | null
    budgetBytes?: number | null
    defaultWorkspacePaths?: readonly string[]
  } = {},
): AgentWorkspaceManager {
  return new AgentWorkspaceManager(root, {
    registry,
    workflowRegistry: options.workflowRegistry ?? null,
    defaultWorkspacePaths: options.defaultWorkspacePaths ?? [],
    getStorageBudgetBytes: () => options.budgetBytes ?? null,
    computeDirectorySize: async (path) => fake.sizes.get(path) ?? 1000,
  })
}

// Write a workflow workspace identity marker (as WorkspaceManager does).
export async function writeWorkflowMarker(workspacePath: string, workflowRunId: string): Promise<void> {
  await mkdir(join(workspacePath, ".mohist"), { recursive: true })
  await writeFile(join(workspacePath, ".mohist", "workspace.json"), JSON.stringify({
    workflowRunId,
    runBranch: `mohist/run-${workflowRunId}`,
  }, null, 2))
}

export async function createSymlinkedDir(target: string, linkPath: string): Promise<void> {
  await mkdir(dirname(linkPath), { recursive: true })
  await mkdir(target, { recursive: true })
  await symlink(target, linkPath)
}

export function validChildSessionId(seed = 1): string {
  return seed.toString(16).padStart(32, "0")
}
