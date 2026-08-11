import type { JsonObject } from "../../src/core/types.js"
import type { ActionTestContext as ActionContext } from "./action-test-context.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../../src/actions/git.js"
import type { RunnerCommandRunner, RunnerFileSystem, RunnerGitRunner } from "../../src/system/filesystem.js"

export type CommandResult = { exitCode: number; stdout: string; stderr: string; status?: "timeout"; timeoutMs?: number }
export type GhCall = { command: string; timeoutMs: number | undefined }

export const WORKSPACE_PATH = "/workspace"
export const PROJECT_PATH = "/project"
export const PR_CHECKS_COMMAND = "gh pr view 42 --json statusCheckRollup"

export type MergeGhTestResources = {
  fileSystem: RunnerFileSystem
  ghCalls: GhCall[]
  githubPrGitRunner?: RunnerGitRunner
  githubPrGhRunner?: RunnerCommandRunner
  issueFieldCommandRunner?: (command: string, args: string[], cwd: string, signal: AbortSignal) => Promise<{ exitCode: number; stdout: string; stderr: string }>
  githubPrChecksTiming?: { pollIntervalMs?: number; noChecksGraceMs?: number; unavailableRetryLimit?: number }
  githubPrTransientRetry?: { limit?: number; backoffMs?: number }
}

export function ghOk(stdout: string, stderr = ""): CommandResult {
  return { exitCode: 0, stdout, stderr }
}

export function ghFail(stderr: string, stdout = "", exitCode = 1): CommandResult {
  return { exitCode, stdout, stderr }
}

export function ghTimeout(): CommandResult {
  return {
    exitCode: 124,
    stdout: "",
    stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
    status: "timeout",
    timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
  }
}

export function checksRollup(checks: unknown[]): string {
  return JSON.stringify({ statusCheckRollup: checks })
}

export function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-merge-1",
    workId: "merge-pr",
    workType: "task",
    stage: "integrate",
    title: "Merge GitHub PR",
    uses: "mohist/merge-github-pr",
     with: { repositoryUrl: "https://github.com/example/repo.git", ...withOverrides },
    variables: {
      project: { id: "proj_1", path: WORKSPACE_PATH },
      issue: { title: "Use GitHub PR workflow", body: "Open, review, and merge a GitHub PR.", number: 248 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "master",
      },
      workspace: { path: WORKSPACE_PATH, branch: "mohist/run-wr-merge-1" },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    projectId: "proj_1",
    issueNumber: 248,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

export function withLog(ctx: ActionContext, writes: Array<{ source: string; text: string }>): ActionContext {
  return {
    ...ctx,
    log: { write: (source: string, text: string) => { writes.push({ source, text }); return writes.length } } as never,
  }
}

export function authoritativeRepository(gitUrl = "https://github.com/acme/repo.git"): JsonObject {
  return {
    repository: {
      name: "repo",
      gitUrl,
      baseBranch: "master",
    },
  }
}

export interface MergeGhTestHarness {
  installGit(resources: MergeGhTestResources, respond: () => never): void
  installGh(resources: MergeGhTestResources, respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>): void
  installMoIssueShow(resources: MergeGhTestResources, title?: string, body?: string): string[]
}

export function createMergeGhTestHarness(): MergeGhTestHarness {
  function installGit(resources: MergeGhTestResources, respond: () => never) {
    resources.githubPrGitRunner = async () => await respond()
  }

  function installGh(resources: MergeGhTestResources, respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>) {
    resources.githubPrGhRunner = async (cmd, args, cwd, _signal, _env, options) => {
      const visibleArgs = args.at(-2) === "--repo" ? args.slice(0, -2) : args
      resources.ghCalls.push({ command: [cmd, ...visibleArgs].join(" "), timeoutMs: options?.timeoutMs })
      return await respond(cmd, visibleArgs, cwd)
    }
  }

  function installMoIssueShow(resources: MergeGhTestResources, title = "Use GitHub PR workflow", body = "body") {
    const calls: string[] = []
    resources.issueFieldCommandRunner = async (cmd, args) => {
      calls.push([cmd, ...args].join(" "))
      return {
        exitCode: 0,
        stdout: JSON.stringify({ success: true, data: { title, body } }),
        stderr: "",
      }
    }
    return calls
  }

  return { installGit, installGh, installMoIssueShow }
}
