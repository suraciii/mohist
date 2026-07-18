import { afterEach } from "vitest"
import type { ActionContext, JsonObject } from "../../src/core/types.js"
import { setIssueFieldCommandRunnerForTest } from "../../src/actions/issue-fields.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../../src/actions/git.js"
import {
  setGitHubPrChecksTimingForTest,
  setGitHubPrGhRunnerForTest,
  setGitHubPrGitRunnerForTest,
  setGitHubPrTransientRetryForTest,
} from "../../src/actions/github-pr.js"

export type CommandResult = { exitCode: number; stdout: string; stderr: string; status?: "timeout"; timeoutMs?: number }
export type GhCall = { command: string; timeoutMs: number | undefined }

export const WORKSPACE_PATH = "/workspace"
export const PROJECT_PATH = "/project"
export const PR_CHECKS_COMMAND = "gh pr view 42 --json statusCheckRollup"

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
    with: withOverrides,
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
  readonly ghCalls: GhCall[]
  installGit(respond: () => never): void
  installGh(respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>): void
  installMoIssueShow(title?: string, body?: string): string[]
}

export function createMergeGhTestHarness(): MergeGhTestHarness {
  const ghCalls: GhCall[] = []

  afterEach(() => {
    setGitHubPrGitRunnerForTest(null)
    setGitHubPrGhRunnerForTest(null)
    setGitHubPrChecksTimingForTest(null)
    setGitHubPrTransientRetryForTest(null)
    setIssueFieldCommandRunnerForTest(null)
    ghCalls.length = 0
  })

  function installGit(respond: () => never) {
    setGitHubPrGitRunnerForTest(async () => await respond())
  }

  function installGh(respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>) {
    setGitHubPrGhRunnerForTest(async (cmd, args, cwd, _signal, _env, options) => {
      ghCalls.push({ command: [cmd, ...args].join(" "), timeoutMs: options?.timeoutMs })
      return await respond(cmd, args, cwd)
    })
  }

  function installMoIssueShow(title = "Use GitHub PR workflow", body = "body") {
    const calls: string[] = []
    setIssueFieldCommandRunnerForTest(async (cmd, args) => {
      calls.push([cmd, ...args].join(" "))
      return {
        exitCode: 0,
        stdout: JSON.stringify({ success: true, data: { title, body } }),
        stderr: "",
      }
    })
    return calls
  }

  return { ghCalls, installGit, installGh, installMoIssueShow }
}
