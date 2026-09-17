import { existsSync as defaultExistsSync } from 'node:fs'
import { NETWORK_COMMAND_TIMEOUT_MS } from '../actions/git.js'
import { runCommand as defaultRunCommand, type CommandLineOptions } from '../system/process.js'
import { currentRunnerResources } from '../system/filesystem.js'
import { resolveWorkspaceQuery, type WorkspaceQuery } from '../runtime/workspace-query.js'
import { validateNamedWorkspaceIdentity } from '../runtime/workspace-entity.js'
import { withManagedRepositoryHandle, withManagedWorkspaceHandle } from '../runtime/workspace-managed.js'
import { parseAheadBehind, parseCommits, parseDiffFiles, parseNumstatTotal } from './git-parsers.js'

export interface WorkspaceGitHandlerDeps {
  resolveQuery: typeof resolveWorkspaceQuery
  runnerRoot?: string
  runCommand?: typeof defaultRunCommand
  pathExists?: typeof defaultExistsSync
  allowUnverifiedWorkspaceQueriesForTest?: boolean
}

export interface WorkspaceGitHandlers {
  getDiff(query: WorkspaceQuery): Promise<unknown>
  getCommits(query: WorkspaceQuery): Promise<unknown>
  getCommitDiff(query: WorkspaceQuery, hash: string): Promise<unknown>
  getWorkspaceStatus(query: WorkspaceQuery): Promise<unknown>
  getFileContent(query: WorkspaceQuery, path: string): Promise<unknown>
}

interface HandlerRegistrar {
  on(method: string, handler: (...args: never[]) => Promise<unknown>): void
}

export function createWorkspaceGitHandlers(deps: WorkspaceGitHandlerDeps): WorkspaceGitHandlers {
  const handlers = new Map<string, (...args: never[]) => Promise<unknown>>()
  registerWorkspaceGitHandlers(
    {
      on(method: string, handler: (...args: never[]) => Promise<unknown>) {
        handlers.set(method, handler)
      },
    },
    deps,
  )
  return {
    getDiff: handlers.get('GetDiff')!,
    getCommits: handlers.get('GetCommits')!,
    getCommitDiff: handlers.get('GetCommitDiff')!,
    getWorkspaceStatus: handlers.get('GetWorkspaceStatus')!,
    getFileContent: handlers.get('GetFileContent')!,
  }
}

export async function git(workDir: string, args: string[], signal: AbortSignal, options?: CommandLineOptions) {
  const runner = currentRunnerResources()?.controlGitRunner ?? defaultRunCommand
  return runner('git', args, workDir, signal, undefined, options)
}

export async function isGitWorkTree(workDir: string, signal: AbortSignal): Promise<boolean> {
  const checker = currentRunnerResources()?.controlExistsChecker ?? defaultExistsSync
  if (!checker(workDir)) return false
  const result = await git(workDir, ['rev-parse', '--is-inside-work-tree'], signal)
  return result.exitCode === 0 && result.stdout.trim() === 'true'
}

function registerWorkspaceGitHandlers(conn: HandlerRegistrar, deps: WorkspaceGitHandlerDeps): void {
  const resolveQuery = deps.resolveQuery
  const resolveForHandler = (query: WorkspaceQuery) => (deps.runnerRoot ? resolveQuery(query, deps.runnerRoot) : null)

  async function runGit(workDir: string, args: string[], signal: AbortSignal, options?: CommandLineOptions) {
    const runner = deps.runCommand ?? currentRunnerResources()?.controlGitRunner ?? defaultRunCommand
    return runner('git', args, workDir, signal, undefined, options)
  }

  async function isWorkTree(workDir: string, signal: AbortSignal): Promise<boolean> {
    const checker = deps.pathExists ?? currentRunnerResources()?.controlExistsChecker ?? defaultExistsSync
    if (!checker(workDir)) return false
    const result = await runGit(workDir, ['rev-parse', '--is-inside-work-tree'], signal)
    return result.exitCode === 0 && result.stdout.trim() === 'true'
  }

  // Resolve the Named Workspace directory from `(projectId, workspaceName)`
  // (never from a Server-supplied path), then validate the on-disk marker and
  // the `REPOS/<repository>` origin inside the managed directory handles so a
  // symlink swap cannot redirect a Git command.
  async function withValidatedRepository<T>(
    query: WorkspaceQuery,
    signal: AbortSignal,
    fallback: T,
    operation: (workDir: string) => Promise<T>,
  ): Promise<T> {
    const resolved = resolveForHandler(query)
    if (!resolved) return fallback
    if (deps.allowUnverifiedWorkspaceQueriesForTest) return await operation(resolved.workDir)
    try {
      return await withManagedWorkspaceHandle(
        deps.runnerRoot!,
        resolved.workspacePath,
        true,
        async (managedWorkspacePath) => {
          await validateNamedWorkspaceIdentity(managedWorkspacePath, resolved.identity, signal)
          return await withManagedRepositoryHandle(managedWorkspacePath, resolved.identity.repositoryName, operation)
        },
      )
    } catch {
      return fallback
    }
  }

  conn.on('GetDiff', async (query: WorkspaceQuery) => {
    const workspace = resolveForHandler(query)
    if (!workspace) return null
    const ac = new AbortController()
    return await withValidatedRepository(query, ac.signal, null, async (workDir) => {
      if (!(await isWorkTree(workDir, ac.signal))) return null
      const branchExists = await runGit(workDir, ['rev-parse', '--verify', `refs/heads/${workspace.head}`], ac.signal)
      if (branchExists.exitCode !== 0) return null
      const [numstat, fullDiff, mergeBaseResult, aheadBehindResult, logResult] = await Promise.all([
        runGit(workDir, ['diff', `${workspace.baseBranch}...${workspace.head}`, '--numstat'], ac.signal),
        runGit(workDir, ['diff', `${workspace.baseBranch}...${workspace.head}`], ac.signal),
        runGit(workDir, ['merge-base', workspace.baseBranch, workspace.head], ac.signal),
        runGit(
          workDir,
          ['rev-list', '--left-right', '--count', `${workspace.baseBranch}...${workspace.head}`],
          ac.signal,
        ),
        runGit(workDir, ['log', `${workspace.baseBranch}...${workspace.head}`, '--format=%H'], ac.signal),
      ])
      const files = parseDiffFiles(numstat.stdout, fullDiff.stdout)
      const mergeBase = mergeBaseResult.exitCode === 0 ? mergeBaseResult.stdout.trim() : workspace.baseBranch
      const commitCount = logResult.exitCode === 0 ? logResult.stdout.trim().split('\n').filter(Boolean).length : 0
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)
      return {
        base: workspace.baseBranch,
        head: workspace.head,
        mergeBase,
        ahead,
        behind,
        commitCount,
        totalAdditions: files.reduce((s, f) => s + f.additions, 0),
        totalDeletions: files.reduce((s, f) => s + f.deletions, 0),
        files,
      }
    })
  })

  conn.on('GetCommits', async (query: WorkspaceQuery) => {
    const workspace = resolveForHandler(query)
    if (!workspace) return null
    const ac = new AbortController()
    return await withValidatedRepository(query, ac.signal, null, async (workDir) => {
      if (!(await isWorkTree(workDir, ac.signal))) return null
      const [logResult, numstat, mergeBaseResult, aheadBehindResult] = await Promise.all([
        runGit(
          workDir,
          ['log', `${workspace.baseBranch}...${workspace.head}`, '--format=%H\t%h\t%s\t%an\t%ad', '--date=iso'],
          ac.signal,
        ),
        runGit(workDir, ['diff', `${workspace.baseBranch}...${workspace.head}`, '--numstat'], ac.signal),
        runGit(workDir, ['merge-base', workspace.baseBranch, workspace.head], ac.signal),
        runGit(
          workDir,
          ['rev-list', '--left-right', '--count', `${workspace.baseBranch}...${workspace.head}`],
          ac.signal,
        ),
      ])
      const commits = parseCommits(logResult.stdout)
      const mergeBase = mergeBaseResult.exitCode === 0 ? mergeBaseResult.stdout.trim() : workspace.baseBranch
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)
      const fileStats = parseNumstatTotal(numstat.stdout)
      return {
        base: workspace.baseBranch,
        head: workspace.head,
        mergeBase,
        ahead,
        behind,
        filesChanged: fileStats.filesChanged,
        totalAdditions: fileStats.additions,
        totalDeletions: fileStats.deletions,
        commits,
      }
    })
  })

  conn.on('GetCommitDiff', async (query: WorkspaceQuery, hash: string) => {
    if (!resolveForHandler(query)) return null
    const ac = new AbortController()
    return await withValidatedRepository(query, ac.signal, null, async (workDir) => {
      if (!(await isWorkTree(workDir, ac.signal))) return null
      const result = await runGit(workDir, ['show', '--format=', '--patch', hash], ac.signal)
      return result.exitCode === 0 ? { diff: result.stdout } : null
    })
  })

  conn.on('GetWorkspaceStatus', async (query: WorkspaceQuery) => {
    const workspace = resolveForHandler(query)
    if (!workspace) return { exists: false }
    const ac = new AbortController()
    return await withValidatedRepository(
      query,
      ac.signal,
      { exists: false, reason: 'workspace_identity_mismatch' },
      async (workDir) => {
        if (!(await isWorkTree(workDir, ac.signal))) return { exists: false }
        const branchExists = await runGit(workDir, ['rev-parse', '--verify', `refs/heads/${workspace.head}`], ac.signal)
        if (branchExists.exitCode !== 0) return { exists: false }
        const rebaseResult = await runGit(workDir, ['rebase', '--show-current-patch'], ac.signal)
        const rebaseInProgress = rebaseResult.exitCode === 0
        let conflictingFiles: string[] = []
        if (rebaseInProgress) {
          const statusResult = await runGit(workDir, ['diff', '--name-only', '--diff-filter=U'], ac.signal)
          conflictingFiles = statusResult.stdout.trim().split('\n').filter(Boolean)
        }
        const baseStatus = {
          exists: true,
          branch: workspace.head,
          baseBranch: workspace.baseBranch,
          rebaseInProgress,
          conflictingFiles,
        }
        const fetchResult = await runGit(workDir, ['fetch', 'origin', workspace.baseBranch], ac.signal, {
          timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
        })
        if (fetchResult.exitCode !== 0) return { ...baseStatus, reason: 'fetch_failed' }
        if (rebaseInProgress) return { ...baseStatus, reason: 'rebase_in_progress' }
        const remoteRef = `origin/${workspace.baseBranch}`
        const aheadBehindResult = await runGit(
          workDir,
          ['rev-list', '--left-right', '--count', `${remoteRef}...${workspace.head}`],
          ac.signal,
        )
        const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)
        return { ...baseStatus, ahead, behind }
      },
    )
  })

  conn.on('GetFileContent', async (query: WorkspaceQuery, path: string) => {
    const workspace = resolveForHandler(query)
    if (!workspace) return { base: null, head: null }
    const ac = new AbortController()
    return await withValidatedRepository(query, ac.signal, { base: null, head: null }, async (workDir) => {
      if (!(await isWorkTree(workDir, ac.signal))) return { base: null, head: null }
      const [baseResult, headResult] = await Promise.all([
        runGit(workDir, ['show', `${workspace.baseBranch}:${path}`], ac.signal),
        runGit(workDir, ['show', `${workspace.head}:${path}`], ac.signal),
      ])
      return {
        base: baseResult.exitCode === 0 ? baseResult.stdout : null,
        head: headResult.exitCode === 0 ? headResult.stdout : null,
      }
    })
  })
}
