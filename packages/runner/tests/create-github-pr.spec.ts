import { describe, expect, it as vitestIt } from 'vitest'
import type { JsonObject } from '../src/core/types.js'
import type { ActionTestContext as ActionContext } from './support/action-test-context.js'
import { callAction } from './support/call-action.js'
import { createDefaultRegistry } from '../src/actions/registry.js'
import type { RunnerCommandRunner, RunnerFileSystem, RunnerResourceContext } from '../src/system/filesystem.js'
import { withTestRunnerResources } from './support/test-resources.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'
import { NETWORK_COMMAND_TIMEOUT_MS } from '../src/actions/git.js'
import { PUBLICATION_LOG_FORMAT } from '../src/actions/publication.js'
import { createGitHubPrAction } from '../src/actions/github-pr.js'
import { omitGitHubClosingReferences } from '../src/actions/github-pr-issue-fields.js'

type CommandResult = { exitCode: number; stdout: string; stderr: string; status?: 'timeout'; timeoutMs?: number }
type GitResponse = {
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
  status?: 'timeout'
  timeoutMs?: number
}
type GitCall = { command: string; timeoutMs: number | undefined }
type GhCall = { command: string; timeoutMs: number | undefined }

const WORKSPACE_PATH = '/workspace'
const PROJECT_PATH = '/project'

type CreateGitHubPrTestResources = {
  fileSystem: RunnerFileSystem
  commandRunner?: NonNullable<RunnerResourceContext['commandRunner']>
  githubPrGhRunner?: RunnerCommandRunner
  issueFieldCommandRunner?: (
    command: string,
    args: string[],
    cwd: string,
    signal: AbortSignal,
  ) => Promise<CommandResult>
  gitCalls: GitCall[]
  ghCalls: GhCall[]
}

function it(name: string, body: (resources: CreateGitHubPrTestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: CreateGitHubPrTestResources = { fileSystem: new MemoryFileSystem(), gitCalls: [], ghCalls: [] }
    await withTestRunnerResources(async () => await body(resources), resources)
  })
}

function ok(stdout: string): GitResponse {
  return { success: true, stdout, stderr: '', exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string, stdout = ''): GitResponse {
  return {
    success: false,
    stdout,
    stderr,
    exitCode: 1,
    combinedOutput: [stdout.trim(), stderr.trim()].filter(Boolean).join('\n'),
  }
}

const MERGE_BASE_SHA = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
const PUBLISHED_COMMIT_SHA = 'cccccccccccccccccccccccccccccccccccccccc'
const MERGE_BASE_COMMAND = `merge-base origin/master ${PUBLISHED_COMMIT_SHA}`
const LOG_COMMAND = `log --format=${PUBLICATION_LOG_FORMAT} ${MERGE_BASE_SHA}..${PUBLISHED_COMMIT_SHA}`

/** One valid commit record in the NUL-delimited publication log format. */
function commitLog(message: string, sha = PUBLISHED_COMMIT_SHA): string {
  return `${sha}\x00Workflow Agent\x00agent@example.com\x00Workflow Agent\x00agent@example.com\x00${message}\x00`
}

const VALID_COMMIT_LOG = commitLog('Use GitHub PR workflow\n\nOpen, review, and merge a GitHub PR.\n')

/** Serves the read-only merge-base/log history validation the action runs before any external write. */
function validHistoryRespond(_workDir: string, args: string[]): GitResponse {
  if (args.join(' ') === 'ls-remote origin refs/heads/mohist/run-wr-gh-pr-1')
    return ok(`${PUBLISHED_COMMIT_SHA}\trefs/heads/mohist/run-wr-gh-pr-1\n`)
  switch (args.join(' ')) {
    case MERGE_BASE_COMMAND:
      return ok(`${MERGE_BASE_SHA}\n`)
    case LOG_COMMAND:
      return ok(VALID_COMMIT_LOG)
    default:
      return fail(`unexpected git call: ${args.join(' ')}`)
  }
}

function ghOk(stdout: string, stderr = ''): CommandResult {
  return { exitCode: 0, stdout, stderr }
}

function ghFail(stderr: string, stdout = '', exitCode = 1): CommandResult {
  return { exitCode, stdout, stderr }
}

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: 'wr-gh-pr-1',
    workId: 'open-draft-pr',
    workType: 'task',
    stage: 'plan',
    title: 'Open or reuse GitHub draft PR',
    uses: 'mohist/create-github-pr',
    with: {
      repositoryUrl: 'https://github.com/example/repo.git',
      source: 'mohist/run-wr-gh-pr-1',
      target: 'master',
      ...withOverrides,
    },
    variables: {
      project: { id: 'proj_1', path: WORKSPACE_PATH },
      issue: { title: 'Use GitHub PR workflow', body: 'Open, review, and merge a GitHub PR.', number: 248 },
      repository: {
        gitUrl: 'https://example.com/repo.git',
        baseBranch: 'master',
      },
      workspace: { name: 'issue-9', branch: 'mohist/run-wr-gh-pr-1' },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    projectId: 'proj_1',
    issueNumber: 248,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function withLog(ctx: ActionContext, writes: Array<{ source: string; text: string }>): ActionContext {
  return {
    ...ctx,
    log: {
      write: (source: string, text: string) => {
        writes.push({ source, text })
        return writes.length
      },
    } as never,
  }
}

function installGit(
  resources: CreateGitHubPrTestResources,
  respond: (workDir: string, args: string[], signal: AbortSignal) => GitResponse | Promise<GitResponse>,
) {
  // The action reads commit history through the generic command runner; gh and
  // mo keep their dedicated runners, so only `git` commands arrive here.
  resources.commandRunner = {
    run: async (command, args, cwd, signal, _env, options) => {
      if (command !== 'git') throw new Error(`unexpected command routed to the git fixture: ${command}`)
      const recorded: GitCall = {
        command: args.join(' '),
        timeoutMs: (options as { timeoutMs?: number } | undefined)?.timeoutMs,
      }
      resources.gitCalls.push(recorded)
      const response = await respond(cwd, args, signal)
      return {
        exitCode: response.exitCode,
        stdout: response.stdout,
        stderr: response.stderr,
        status: response.status,
        timeoutMs: response.timeoutMs,
      }
    },
  }
}

function installGh(
  resources: CreateGitHubPrTestResources,
  respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>,
) {
  resources.githubPrGhRunner = async (cmd, args, cwd, _signal, _env, options) => {
    const visibleArgs = args.at(-2) === '--repo' ? args.slice(0, -2) : args
    const recorded: GhCall = { command: [cmd, ...visibleArgs].join(' '), timeoutMs: options?.timeoutMs }
    resources.ghCalls.push(recorded)
    return await respond(cmd, visibleArgs, cwd)
  }
}

function installMoIssueShow(
  resources: CreateGitHubPrTestResources,
  title = 'Use GitHub PR workflow',
  body = 'Open, review, and merge a GitHub PR.',
) {
  const calls: string[] = []
  resources.issueFieldCommandRunner = async (cmd, args) => {
    calls.push([cmd, ...args].join(' '))
    return {
      exitCode: 0,
      stdout: JSON.stringify({ success: true, data: { title, body } }),
      stderr: '',
    }
  }
  return calls
}

describe('mohist/create-github-pr registry', () => {
  it('registers create-github-pr and exposes it under the new id only', () => {
    const registry = createDefaultRegistry()
    const resolved = registry.resolve('mohist/create-github-pr')
    expect(resolved.kind).toBe('definition')
    if (resolved.kind === 'definition') {
      expect(resolved.definition.manifest.name).toBe('mohist/create-github-pr')
      expect(resolved.definition.manifest.inputs['repositoryUrl']).toMatchObject({ types: ['string'], required: true })
      expect(resolved.definition.manifest.inputs['source']).toMatchObject({ types: ['string'], required: true })
      expect(resolved.definition.manifest.inputs['target']).toMatchObject({ types: ['string'], required: true })
      expect(resolved.definition.manifest.inputs['remote']).toBeUndefined()
    }
    expect(registry.resolve('mohist/create-pull-request').kind).toBe('unknown')
    expect(registry.resolve('mohist/publish-via-pr').kind).toBe('unknown')
  })
})

describe('mohist/create-github-pr action', () => {
  it('opens a draft PR from an already-published workflow branch', async (resources) => {
    const gitCalls: string[] = []
    const ghCalls: string[] = []
    const moCalls = installMoIssueShow(resources)

    installGit(resources, (_workDir, args) => {
      gitCalls.push(args.join(' '))
      return validHistoryRespond(_workDir, args)
    })

    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      ghCalls.push(full)
      switch (full) {
        case 'gh --version':
          return ghOk('gh version 2.55.0\n')
        case 'gh auth status':
          return ghOk('authenticated\n')
        case 'gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft':
          return ghOk('[]\n')
        case 'gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft':
          return ghOk('https://github.com/example/repo/pull/42\n')
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(
      createGitHubPrAction,
      context({
        source: 'mohist/run-wr-gh-pr-1',
        target: 'master',
        remote: 'origin',
        titleFrom: 'issue.title',
        bodyFrom: 'issue.body',
      }),
    )
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(gitCalls).toEqual([
      'ls-remote origin refs/heads/mohist/run-wr-gh-pr-1',
      MERGE_BASE_COMMAND,
      LOG_COMMAND,
      'ls-remote origin refs/heads/mohist/run-wr-gh-pr-1',
    ])
    expect(ghCalls).toEqual([
      'gh --version',
      'gh auth status',
      'gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft',
      'gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft',
    ])
    expect(moCalls).toEqual(['mo issue view 248 --project proj_1 --json title,body'])
    expect(output).toMatchObject({
      kind: 'create-github-pr',
      status: 'completed',
      branch: 'mohist/run-wr-gh-pr-1',
      prNumber: 42,
      prUrl: 'https://github.com/example/repo/pull/42',
      operation: 'created',
      draft: true,
      output: 'https://github.com/example/repo/pull/42',
    })
  })

  it('omits GitHub closing references from the PR body', async (resources) => {
    let createArgs: string[] | undefined
    installGit(resources, validHistoryRespond)
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      if (full === 'gh --version' || full === 'gh auth status') return ghOk('ok\n')
      if (args.join(' ').startsWith('pr list ')) return ghOk('[]\n')
      if (args[0] === 'pr' && args[1] === 'create') {
        createArgs = args
        return ghOk('https://github.com/example/repo/pull/42\n')
      }
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await callAction(
      createGitHubPrAction,
      context({
        title: 'Issue title',
        body: 'Fixes #248\n\nCloses acme/widgets#12\n\nResolves https://github.com/acme/widgets/issues/13',
      }),
    )

    expect(result.error).toBeUndefined()
    const body = createArgs?.[createArgs.indexOf('--body') + 1]
    expect(body).toBeDefined()
    expect(body).not.toMatch(/\b(?:close[sd]?|fix(?:es|ed)?|resolve[sd]?)\s+(?:#|[A-Za-z0-9_.-]+\/)/i)
    expect(body).not.toContain('https://github.com/acme/widgets/issues/13')
  })

  it('sanitizes every GitHub closing keyword on both create and edit paths', async (resources) => {
    const keywords = ['close', 'closes', 'closed', 'fix', 'fixes', 'fixed', 'resolve', 'resolves', 'resolved']
    const sourceBody = keywords
      .flatMap((keyword, index) => [
        `${keyword} #${index + 1}`,
        `${keyword} acme/widgets#${index + 100}`,
        `${keyword} https://github.com/acme/widgets/issues/${index + 200}`,
      ])
      .join('\n')
    let listCalls = 0
    let createdBody: string | undefined
    let editedBody: string | undefined
    installGit(resources, validHistoryRespond)
    installGh(resources, (cmd, args) => {
      if (cmd === 'gh' && args[0] === '--version') return ghOk('ok\n')
      if (cmd === 'gh' && args.join(' ') === 'auth status') return ghOk('ok\n')
      if (cmd === 'gh' && args[0] === 'pr' && args[1] === 'list') {
        listCalls += 1
        return listCalls === 1
          ? ghOk('[]\n')
          : ghOk(JSON.stringify([{ number: 42, url: 'https://github.com/acme/repo/pull/42', isDraft: true }]))
      }
      if (cmd === 'gh' && args[0] === 'pr' && args[1] === 'create') {
        createdBody = args[args.indexOf('--body') + 1]
        return ghOk('https://github.com/acme/repo/pull/42\n')
      }
      if (cmd === 'gh' && args[0] === 'pr' && args[1] === 'edit') {
        editedBody = args[args.indexOf('--body') + 1]
        return ghOk('ok\n')
      }
      return ghFail(`unexpected gh call: ${[cmd, ...args].join(' ')}`)
    })

    const input = context({ title: 'Issue title', body: sourceBody })
    const created = await callAction(createGitHubPrAction, input)
    const edited = await callAction(createGitHubPrAction, input)

    expect(created.error).toBeUndefined()
    expect(edited.error).toBeUndefined()
    expect(createdBody).toBe(omitGitHubClosingReferences(sourceBody))
    expect(editedBody).toBe(omitGitHubClosingReferences(sourceBody))
  })

  it('uses the explicitly declared repository despite different Variables', async (resources) => {
    const prArguments: string[][] = []
    installGit(resources, validHistoryRespond)
    installGh(resources, (_cmd, args) => {
      if (args[0] === '--version' || args.join(' ') === 'auth status') return ghOk('ok\n')
      if (args[0] === 'pr') prArguments.push(args)
      if (args.join(' ').startsWith('pr list ')) return ghOk('[]\n')
      if (args.join(' ').startsWith('pr create ')) return ghOk('https://github.com/acme/repo/pull/42\n')
      return ghFail(`unexpected gh call: ${args.join(' ')}`)
    })

    const result = await callAction(
      createGitHubPrAction,
      context(
        {
          repositoryUrl: 'https://github.com/acme/repo.git',
          source: 'mohist/run-wr-gh-pr-1',
          target: 'master',
          title: 'Issue title',
          body: 'Issue body',
        },
        { repository: { gitUrl: 'https://example.com/other.git', baseBranch: 'other' } },
      ),
    )

    expect(result.error).toBeUndefined()
    expect(prArguments).toHaveLength(2)
    expect(prArguments.every((args) => args[0] === 'pr')).toBe(true)
  })

  it('rejects an invalid explicit repository URL', async (resources) => {
    const result = await callAction(
      createGitHubPrAction,
      context({
        repositoryUrl: 'not a Git URL',
        source: 'mohist/run-wr-gh-pr-1',
        target: 'master',
        title: 'Issue title',
        body: 'Issue body',
      }),
    )
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'config-error' })
    expect(result.error?.message).toContain('valid GitHub repository URL')
  })

  it('forwards gh command output to the task log sink', async (resources) => {
    const writes: Array<{ source: string; text: string }> = []
    installMoIssueShow(resources)
    installGit(resources, validHistoryRespond)
    resources.githubPrGhRunner = async (cmd, args, cwd, _signal, _env, options) => {
      const full = [cmd, ...args].join(' ')
      options?.onLine?.(`captured ${full}`)
      if (full === 'gh --version') return ghOk('gh version 2.0.0\n')
      if (full === 'gh auth status') return ghOk('Logged in\n')
      if (full.startsWith('gh pr list')) return ghOk('[]\n')
      if (full.startsWith('gh pr create')) return ghOk('https://github.com/acme/repo/pull/42\n')
      return ghFail(`unexpected gh call in ${cwd}: ${full}`)
    }

    const result = await callAction(createGitHubPrAction, withLog(context({ target: 'master' }), writes))

    expect(result.error).toBeUndefined()
    expect(
      writes.some((write) => write.source === 'action:create-github-pr' && write.text.includes('gh pr create')),
    ).toBe(true)
  })

  it('reuses an existing open PR without mutating title/body when gh pr list returns a match', async (resources) => {
    const ghCalls: string[] = []
    installMoIssueShow(resources, 'Fresh issue title', 'Fresh issue body')
    installGit(resources, validHistoryRespond)
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      ghCalls.push(full)
      switch (full) {
        case 'gh --version':
        case 'gh auth status':
          return ghOk('ok\n')
        case 'gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft':
          return ghOk(JSON.stringify([{ number: 7, url: 'https://github.com/example/repo/pull/7', isDraft: true }]))
        case 'gh pr edit 7 --title Fresh issue title --body Fresh issue body':
          return ghOk('')
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(
      createGitHubPrAction,
      context({
        source: 'mohist/run-wr-gh-pr-1',
        target: 'master',
        remote: 'origin',
        titleFrom: 'issue.title',
        bodyFrom: 'issue.body',
      }),
    )
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(ghCalls).toContain('gh pr edit 7 --title Fresh issue title --body Fresh issue body')
    expect(ghCalls.some((call) => call.startsWith('gh pr create '))).toBe(false)
    expect(output).toMatchObject({
      kind: 'create-github-pr',
      status: 'completed',
      operation: 'reused',
      prNumber: 7,
      prUrl: 'https://github.com/example/repo/pull/7',
      draft: true,
    })
  })

  it('binds every GitHub invocation to the workspace path even when project.path differs', async (resources) => {
    const gitCalls: Array<{ workDir: string; command: string }> = []
    const ghCalls: Array<{ cwd: string; command: string }> = []

    installGit(resources, (workDir, args) => {
      const command = args.join(' ')
      gitCalls.push({ workDir, command })
      return validHistoryRespond(workDir, args)
    })
    installGh(resources, (cmd, args, cwd) => {
      const command = [cmd, ...args].join(' ')
      ghCalls.push({ cwd, command })
      switch (command) {
        case 'gh --version':
        case 'gh auth status':
          return ghOk('ok\n')
        case 'gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft':
          return ghOk('[]\n')
        case 'gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Issue title --body Issue body --draft':
          return ghOk('https://github.com/example/repo/pull/42\n')
        default:
          return ghFail(`unexpected gh call: ${command}`)
      }
    })

    const result = await callAction(
      createGitHubPrAction,
      context(
        {
          source: 'mohist/run-wr-gh-pr-1',
          target: 'master',
          remote: 'origin',
          title: 'Issue title',
          body: 'Issue body',
        },
        { project: { id: 'proj_1', path: PROJECT_PATH } },
      ),
    )
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(ghCalls.map((call) => call.cwd)).toEqual([WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH])
    expect(ghCalls.some((call) => call.cwd === PROJECT_PATH)).toBe(false)
    expect(gitCalls.map((call) => call.workDir)).toEqual([
      WORKSPACE_PATH,
      WORKSPACE_PATH,
      WORKSPACE_PATH,
      WORKSPACE_PATH,
    ])
    expect(gitCalls.map((call) => call.command)).toEqual([
      'ls-remote origin refs/heads/mohist/run-wr-gh-pr-1',
      MERGE_BASE_COMMAND,
      LOG_COMMAND,
      'ls-remote origin refs/heads/mohist/run-wr-gh-pr-1',
    ])
    expect(output.prNumber).toBe(42)
  })

  it('reports unsupported issue field sources as errorCode config-error', async (resources) => {
    installGit(resources, () => fail('git should not be called'))
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      switch (full) {
        case 'gh --version':
        case 'gh auth status':
          return ghOk('ok\n')
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(
      createGitHubPrAction,
      context({
        titleFrom: 'issue.summary',
      }),
    )
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'config-error' })
    expect(result.error?.message).toContain("Unsupported titleFrom source 'issue.summary'")
  })

  it('blocks a PR write when its published source moves after commit validation', async (resources) => {
    let reads = 0
    installGit(resources, (workDir, args) => {
      if (args[0] === 'ls-remote' && ++reads === 2) return ok(`${MERGE_BASE_SHA}\trefs/heads/mohist/run-wr-gh-pr-1\n`)
      return validHistoryRespond(workDir, args)
    })
    installGh(resources, (cmd, args) =>
      [cmd, ...args].join(' ') === 'gh --version' || [cmd, ...args].join(' ') === 'gh auth status'
        ? ghOk('ok')
        : ghFail('PR write should not run'),
    )
    const result = await callAction(createGitHubPrAction, context({ title: 'Issue title', body: 'Issue body' }))
    expect(result.error).toMatchObject({
      code: 'publication-validation-failed',
      message: expect.stringContaining('moved'),
    })
    expect(resources.ghCalls.map((call) => call.command)).toEqual(['gh --version', 'gh auth status'])
  })

  it('performs only read-only history-validation Git calls when GitHub creates the PR', async (resources) => {
    installGit(resources, validHistoryRespond)
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      if (full === 'gh --version' || full === 'gh auth status') return ghOk('ok\n')
      if (full.startsWith('gh pr list ')) return ghOk('[]\n')
      if (full.startsWith('gh pr create ')) return ghOk('https://github.com/example/repo/pull/42\n')
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await callAction(
      createGitHubPrAction,
      context({
        source: 'mohist/run-wr-gh-pr-1',
        target: 'master',
        remote: 'origin',
        title: 'Issue title',
        body: 'Issue body',
      }),
    )
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output.operation).toBe('created')
    expect(resources.gitCalls.map((call) => call.command)).toEqual([
      'ls-remote origin refs/heads/mohist/run-wr-gh-pr-1',
      MERGE_BASE_COMMAND,
      LOG_COMMAND,
      'ls-remote origin refs/heads/mohist/run-wr-gh-pr-1',
    ])
  })

  it('reports config-error when the gh CLI precheck fails', async (resources) => {
    installGit(resources, () => fail('git should not be called'))
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      if (full === 'gh --version') {
        return ghFail('gh: command not found', '', 127)
      }
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await callAction(
      createGitHubPrAction,
      context({
        title: 'Issue title',
        body: 'Issue body',
      }),
    )
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'config-error' })
    expect(result.error?.message).toContain('gh CLI is not installed')
  })

  it('does not pass --draft when draft is explicitly false', async (resources) => {
    const ghCalls: string[] = []
    installGit(resources, validHistoryRespond)
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      ghCalls.push(full)
      switch (full) {
        case 'gh --version':
        case 'gh auth status':
          return ghOk('ok\n')
        case 'gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft':
          return ghOk('[]\n')
        case 'gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR.':
          return ghOk('https://github.com/example/repo/pull/42\n')
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(
      createGitHubPrAction,
      context({
        source: 'mohist/run-wr-gh-pr-1',
        target: 'master',
        remote: 'origin',
        title: 'Use GitHub PR workflow',
        body: 'Open, review, and merge a GitHub PR.',
        draft: false,
      }),
    )
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(ghCalls.some((call) => call.startsWith('gh pr create ') && call.endsWith('--draft'))).toBe(false)
    expect(ghCalls.some((call) => call.startsWith('gh pr create '))).toBe(true)
    expect(output).toMatchObject({
      draft: false,
      operation: 'created',
    })
  })

  it('NetworkGitHubCommands_AllReceiveTimeoutMs', async (resources) => {
    installMoIssueShow(resources)
    installGit(resources, validHistoryRespond)
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      switch (full) {
        case 'gh --version':
          return ghOk('gh version 2.0.0\n')
        case 'gh auth status':
          return ghOk('Logged in\n')
        case 'gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft':
          return ghOk('[]\n')
        case 'gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft':
          return ghOk('https://github.com/example/repo/pull/42\n')
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    await callAction(
      createGitHubPrAction,
      context({
        source: 'mohist/run-wr-gh-pr-1',
        target: 'master',
        remote: 'origin',
        titleFrom: 'issue.title',
        bodyFrom: 'issue.body',
      }),
    )

    for (const command of [
      'gh --version',
      'gh auth status',
      'gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft',
      'gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft',
    ]) {
      const call = resources.ghCalls.find((c) => c.command === command)
      expect(call?.timeoutMs, `gh call ${command} missing timeoutMs`).toBe(NETWORK_COMMAND_TIMEOUT_MS)
    }

    for (const command of ['ls-remote origin refs/heads/mohist/run-wr-gh-pr-1', MERGE_BASE_COMMAND, LOG_COMMAND]) {
      const call = resources.gitCalls.find((c) => c.command === command)
      expect(call?.timeoutMs, `git call ${command} missing timeoutMs`).toBe(NETWORK_COMMAND_TIMEOUT_MS)
    }
    expect(resources.gitCalls).toHaveLength(4)
  })

  it('GhPrCreateTimeout_ClassifiesAsRetrySafeAndSurfacesDuration', async (resources) => {
    installMoIssueShow(resources)
    installGit(resources, validHistoryRespond)
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      switch (full) {
        case 'gh --version':
          return ghOk('gh version 2.0.0\n')
        case 'gh auth status':
          return ghOk('Logged in\n')
        case 'gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft':
          return ghOk('[]\n')
        case 'gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft':
          return {
            exitCode: 124,
            stdout: '',
            stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
            status: 'timeout' as const,
            timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
          }
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(
      createGitHubPrAction,
      context({
        source: 'mohist/run-wr-gh-pr-1',
        target: 'master',
        remote: 'origin',
        titleFrom: 'issue.title',
        bodyFrom: 'issue.body',
      }),
    )
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: 'timeout' })
    expect(result.error?.message).toContain('timed out')
  })

  it('blocks PR creation when a source commit carries a literal escaped newline under the default policy', async (resources) => {
    installGit(resources, (_workDir, args) => {
      if (args[0] === 'ls-remote') return validHistoryRespond(_workDir, args)
      switch (args.join(' ')) {
        case MERGE_BASE_COMMAND:
          return ok(`${MERGE_BASE_SHA}\n`)
        case LOG_COMMAND:
          return ok(commitLog('Add the workflow step\\n\\nwith escaped newlines'))
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      switch (full) {
        case 'gh --version':
        case 'gh auth status':
          return ghOk('ok\n')
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(createGitHubPrAction, context({ title: 'Issue title', body: 'Issue body' }))

    expect(result.error).toMatchObject({ code: 'publication-validation-failed' })
    expect(result.error?.message).toContain('literal escaped newline')
    expect(result.error?.message).toContain('No PR was written')
    expect(resources.ghCalls.map((call) => call.command)).toEqual(['gh --version', 'gh auth status'])
  })

  it('does not let an embedded record separator smuggle a malformed trailer past the commit gate', async (resources) => {
    installGit(resources, (_workDir, args) => {
      if (args[0] === 'ls-remote') return validHistoryRespond(_workDir, args)
      switch (args.join(' ')) {
        case MERGE_BASE_COMMAND:
          return ok(`${MERGE_BASE_SHA}\n`)
        case LOG_COMMAND:
          return ok(commitLog('Implement the workflow step\n\nPolicy\x1e: yes'))
        default:
          return fail(`unexpected git call: ${args.join(' ')}`)
      }
    })
    installGh(resources, (cmd, args) => {
      const full = [cmd, ...args].join(' ')
      switch (full) {
        case 'gh --version':
        case 'gh auth status':
          return ghOk('ok\n')
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(
      createGitHubPrAction,
      context({ title: 'Issue title', body: 'Issue body', requiredTrailers: ['Policy'] }),
    )

    expect(result.error).toMatchObject({ code: 'publication-validation-failed' })
    expect(result.error?.message).toContain('Malformed trailer line')
    expect(result.error?.message).toContain('No PR was written')
    expect(resources.ghCalls.map((call) => call.command)).toEqual(['gh --version', 'gh auth status'])
  })
})
