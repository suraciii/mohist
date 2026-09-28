import type { ActionResult, JsonObject } from '../core/types.js'
import type { ActionHost } from './host.js'
import { booleanInput, stringInput } from '../core/json.js'
import { git as defaultGit, NETWORK_COMMAND_TIMEOUT_MS, type GitOptions } from './git.js'
import {
  PUBLICATION_LOG_FORMAT,
  formatPublicationValidationErrors,
  parsePublicationCommits,
  publicationPolicyFromInputs,
  resolvePublicationStrategy,
  unsupportedStrategyMessage,
  validatePublicationCommits,
  type PublicationPolicy,
  type PublicationStrategy,
} from './publication.js'
import { timeoutStepMetadata, type GitHubPrStep } from './github-pr-types.js'
import { fail, succeed } from './action-result.js'
import { currentRunnerResources, type RunnerGitRunner } from '../system/filesystem.js'

type GitRunner = RunnerGitRunner
type GitResult = Awaited<ReturnType<GitRunner>>

function git(workDir: string, args: string[], signal: AbortSignal, options?: GitOptions): Promise<GitResult> {
  return (currentRunnerResources()?.pushGitRunner ?? currentRunnerResources()?.gitRunner ?? defaultGit)(
    workDir,
    args,
    signal,
    options,
  )
}

export type PushGitResult = GitResult

const ACTION_SOURCE = 'action:push'

function sinkOptions(host: ActionHost): GitOptions | undefined {
  return host.log ? { sink: { log: host.log, source: ACTION_SOURCE } } : undefined
}

function networkOptions(host: ActionHost): GitOptions | undefined {
  if (!host.log) return { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
  return { sink: { log: host.log, source: ACTION_SOURCE }, timeoutMs: NETWORK_COMMAND_TIMEOUT_MS }
}

/**
 * `partial-git` publishes from the existing workspace clone. `full-clone`
 * first requests all objects from the configured remote, which makes commit
 * validation safe for partial/promisor clones. There is deliberately no
 * implicit fallback between strategies.
 */
const PUSH_SUPPORTED_STRATEGIES: readonly PublicationStrategy[] = ['partial-git', 'full-clone']

export async function pushAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const source = stringInput(inputs, 'source')
  const target = stringInput(inputs, 'target')
  const remote = stringInput(inputs, 'remote')
  if (!source) return fail('invalid-input', "Push requires input 'source'")
  if (!target) return fail('invalid-input', "Push requires input 'target'")
  if (!remote) return fail('invalid-input', "Push requires input 'remote'")
  const force = booleanInput(inputs, 'force') === true
  const forceWithLease = !force && booleanInput(inputs, 'forceWithLease') === true
  const refspec = `${source}:${target}`
  const strategy = resolvePublicationStrategy(inputs, PUSH_SUPPORTED_STRATEGIES, 'partial-git')
  if (strategy.kind === 'unsupported') {
    return pushOutput(
      source,
      target,
      remote,
      host.workDir,
      null,
      false,
      force,
      forceWithLease,
      unsupportedStrategyMessage('mohist/push', strategy.requested, strategy.supported),
      'publication-strategy-unsupported',
      1,
      [],
      false,
      'partial-git',
    )
  }
  const workDir = host.workDir
  const networkOpts = networkOptions(host)
  const steps: GitHubPrStep[] = []
  if (strategy.strategy === 'full-clone') {
    const fetchArgs = ['fetch', '--no-filter', remote]
    const fetch = await git(workDir, fetchArgs, host.signal, networkOpts)
    steps.push({
      name: 'git-fetch-full-clone',
      command: fetchArgs.join(' '),
      exitCode: fetch.exitCode,
      output: fetch.combinedOutput,
      ...timeoutStepMetadata(fetch),
    })
    if (!fetch.success) {
      return pushOutput(
        source,
        target,
        remote,
        host.workDir,
        null,
        false,
        force,
        forceWithLease,
        `Full-clone publication could not obtain complete objects: ${fetch.combinedOutput || 'git fetch failed'}. No push was performed.`,
        'publication-full-clone-failed',
        fetch.exitCode,
        steps,
        false,
        strategy.strategy,
      )
    }
  }
  const opts = sinkOptions(host)
  const strategyName = strategy.strategy
  const sourceResolve = await git(workDir, ['rev-parse', source], host.signal, opts)
  if (!sourceResolve.success) {
    return pushOutput(
      source,
      target,
      remote,
      workDir,
      null,
      false,
      force,
      forceWithLease,
      sourceResolve.combinedOutput,
      sourceResolve.status === 'timeout' ? 'timeout' : 'push-failed',
      sourceResolve.exitCode,
      steps,
      false,
      strategyName,
    )
  }
  const landedCommit = sourceResolve.stdout.trim()
  const remoteBefore = await resolveRemoteTip(workDir, remote, target, host.signal, networkOpts)
  if (remoteBefore.kind === 'timeout' && forceWithLease) {
    steps.push({
      name: 'git-ls-remote',
      command: remoteBefore.command,
      exitCode: remoteBefore.result.exitCode,
      output: remoteBefore.result.combinedOutput,
      ...timeoutStepMetadata(remoteBefore.result),
    })
    return pushOutput(
      source,
      target,
      remote,
      workDir,
      landedCommit,
      false,
      force,
      forceWithLease,
      remoteBefore.result.combinedOutput,
      'timeout',
      remoteBefore.result.exitCode,
      steps,
    )
  }

  const validation = await validateCommitsBeforePush(
    workDir,
    landedCommit,
    remote,
    typeof inputs['baseBranch'] === 'string' ? inputs['baseBranch'] : target,
    publicationPolicyFromInputs(inputs),
    host.signal,
    opts,
    networkOpts,
  )
  if (validation) {
    return pushOutput(
      source,
      target,
      remote,
      workDir,
      landedCommit,
      false,
      force,
      forceWithLease,
      validation.message,
      validation.code,
      validation.exitCode,
      steps,
      false,
      strategyName,
    )
  }
  // Publish precisely the commit whose metadata passed the gate, not a branch that may have moved.
  const publishedRefspec = `${landedCommit}:refs/heads/${target}`

  const pushArgs = ['push']
  if (force) {
    pushArgs.push('--force')
  } else if (forceWithLease) {
    if (remoteBefore.kind === 'failed' || remoteBefore.kind === 'timeout') {
      pushArgs.push('--force-with-lease')
    } else if (remoteBefore.tip) {
      pushArgs.push(`--force-with-lease=${target}:${remoteBefore.tip}`)
    }
  }
  pushArgs.push(remote, publishedRefspec)
  const push = await git(workDir, pushArgs, host.signal, networkOpts)
  steps.push({
    name: 'git-push',
    command: pushArgs.join(' '),
    exitCode: push.exitCode,
    output: push.combinedOutput,
    ...timeoutStepMetadata(push),
  })
  if (!push.success) {
    const failureCode = looksLikeNonFastForward(push.combinedOutput)
      ? 'base-moved'
      : push.status === 'timeout'
        ? 'timeout'
        : 'push-failed'
    return pushOutput(
      source,
      target,
      remote,
      workDir,
      landedCommit,
      false,
      force,
      forceWithLease,
      push.combinedOutput,
      failureCode,
      push.exitCode,
      steps,
    )
  }

  const remoteAfter = await resolveRemoteTip(workDir, remote, target, host.signal, networkOpts)
  const updated =
    remoteAfter.kind === 'resolved' &&
    remoteAfter.tip === landedCommit &&
    (remoteBefore.kind !== 'resolved' || remoteBefore.tip !== landedCommit)
  if (remoteAfter.kind === 'timeout') {
    steps.push({
      name: 'git-ls-remote-after-push',
      command: remoteAfter.command,
      exitCode: remoteAfter.result.exitCode,
      output: remoteAfter.result.combinedOutput,
      ...timeoutStepMetadata(remoteAfter.result),
    })
  }

  return pushOutput(
    source,
    target,
    remote,
    workDir,
    landedCommit,
    true,
    force,
    forceWithLease,
    push.combinedOutput,
    null,
    push.exitCode,
    steps,
    updated,
    strategyName,
  )
}

type PushFailureCode =
  | 'base-moved'
  | 'push-failed'
  | 'timeout'
  | 'publication-strategy-unsupported'
  | 'publication-full-clone-failed'
  | 'publication-validation-failed'
  | 'publication-validation-unavailable'
  | null

async function validateCommitsBeforePush(
  workDir: string,
  source: string,
  remote: string,
  baseBranch: string,
  policy: PublicationPolicy,
  signal: AbortSignal,
  localOpts?: GitOptions,
  networkOpts?: GitOptions,
): Promise<{ code: Exclude<PushFailureCode, null>; message: string; exitCode: number | null } | null> {
  const mergeBase = await git(workDir, ['merge-base', `${remote}/${baseBranch}`, source], signal, localOpts)
  if (!mergeBase.success || !mergeBase.stdout.trim()) {
    return {
      code: 'publication-validation-unavailable',
      message:
        `Push blocked: commits could not be read for publication validation (${mergeBase.combinedOutput || 'git merge-base failed'}). ` +
        'Complete the clone or choose an explicit publication strategy that supports this repository. No push was performed.',
      exitCode: mergeBase.exitCode,
    }
  }
  const range = [`${mergeBase.stdout.trim()}..${source}`]
  const log = await git(workDir, ['log', `--format=${PUBLICATION_LOG_FORMAT}`, ...range], signal, networkOpts)
  if (!log.success) {
    return {
      code: 'publication-validation-unavailable',
      message:
        `Push blocked: commits could not be read for publication validation (${log.combinedOutput || 'git log failed'}). ` +
        'On a partial clone this means required objects are missing locally; complete the clone (for example `git fetch origin`) and retry. No push was performed.',
      exitCode: log.exitCode,
    }
  }
  let commits
  try {
    commits = parsePublicationCommits(log.stdout)
  } catch (error) {
    return {
      code: 'publication-validation-unavailable',
      message: `Push blocked: malformed commit history (${String(error)}). No push was performed.`,
      exitCode: 1,
    }
  }
  const errors = validatePublicationCommits(commits, policy)
  if (errors.length === 0) return null
  return {
    code: 'publication-validation-failed',
    message: `Push blocked by publication validation:\n${formatPublicationValidationErrors(errors)}\nFix the commits and retry. No push was performed.`,
    exitCode: 1,
  }
}

function pushOutput(
  source: string,
  target: string,
  remote: string,
  workDir: string,
  landedCommit: string | null,
  pushed: boolean,
  force: boolean,
  forceWithLease: boolean,
  gitOutput: string,
  failureCode: PushFailureCode,
  exitCode: number | null,
  steps: GitHubPrStep[] = [],
  updated = false,
  strategy: PublicationStrategy = 'partial-git',
): ActionResult {
  if (!pushed) {
    const message =
      failureCode === 'base-moved'
        ? 'Push failed because the target branch moved (non-fast-forward). Rebase and try again.'
        : failureCode === 'timeout'
          ? 'Push timed out.'
          : failureCode === 'publication-strategy-unsupported' ||
              failureCode === 'publication-full-clone-failed' ||
              failureCode === 'publication-validation-failed' ||
              failureCode === 'publication-validation-unavailable'
            ? gitOutput
            : `Push failed: ${gitOutput || 'unknown error'}`
    return fail(failureCode ?? 'push-failed', message, { exitCode: exitCode ?? 1 })
  }
  const output: JsonObject = {
    kind: 'push',
    status: 'completed',
    strategy,
    source,
    target,
    remote,
    refspec: `${source}:${target}`,
    workDir,
    landedCommit,
    pushed,
    updated,
    force,
    forceWithLease,
    output: gitOutput,
    steps: steps as unknown as JsonObject,
  }
  return succeed(output, { exitCode: exitCode ?? 0 })
}

function looksLikeNonFastForward(text: string) {
  return (
    /non[-\s]?fast-forward|fetch first/i.test(text) ||
    /!\s*\[rejected\][^\n]*\((stale info|stale|fetch first|non[-\s]?fast-forward|behind[^\)]*)\)/i.test(text)
  )
}

async function resolveRemoteTip(
  workDir: string,
  remote: string,
  target: string,
  signal: AbortSignal,
  opts?: GitOptions,
): Promise<
  { kind: 'resolved'; tip: string } | { kind: 'failed' } | { kind: 'timeout'; command: string; result: GitResult }
> {
  const args = ['ls-remote', remote, `refs/heads/${target}`]
  const probe = await git(workDir, args, signal, opts)
  if (!probe.success) {
    if (probe.status === 'timeout') return { kind: 'timeout', command: args.join(' '), result: probe }
    return { kind: 'failed' }
  }
  const firstLine = probe.stdout.split(/\r?\n/)[0] ?? ''
  return { kind: 'resolved', tip: firstLine.trim().split(/\s+/)[0] ?? '' }
}
