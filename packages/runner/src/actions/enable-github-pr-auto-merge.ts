import { numberInput, stringInput } from '../core/json.js'
import type { ActionResult, JsonObject } from '../core/types.js'
import type { ActionHost } from './host.js'
import type { CommandLineOptions, CommandResult } from '../system/process.js'
import { NETWORK_COMMAND_TIMEOUT_MS } from './git.js'
import { combinedGhOutput } from './github-pr-parse.js'
import { parsePrStatusCheckRollupResult, classifyPrChecks } from './github-pr-checks.js'
import { delayWithSignal, withGitHubRepository } from './github-pr-checks-wait.js'
import { getGitHubPrGh } from './github-pr-runtime.js'
import { parseGitHubRepository } from './github-pr-repository.js'
import { currentRunnerResources, type RunnerCommandRunner } from '../system/filesystem.js'
import { fail, succeed } from './action-result.js'
import { timeoutStepMetadata, type GitHubPrStep } from './github-pr-types.js'
import { looksLikeRetrySafe } from './github-pr-classify.js'

const ACTION_SOURCE = 'action:enable-github-pr-auto-merge'
const DEFAULT_WAIT_MS = 30 * 60_000
const DEFAULT_POLL_MS = 15_000
const DEFAULT_RETRY_LIMIT = 3
const DEFAULT_RETRY_BACKOFF_MS = 2_000
const VIEW_FIELDS = 'state,url,title,mergeStateStatus,mergeCommit,autoMergeRequest,statusCheckRollup'

type View = {
  state: 'OPEN' | 'CLOSED' | 'MERGED'
  url: string
  title: string
  mergeStateStatus: string
  mergeCommit: { oid: string } | null
  autoMergeRequest: Record<string, unknown> | null
  statusCheckRollup: unknown[]
}

type Timing = {
  now: () => number
  delay: (ms: number, signal: AbortSignal) => Promise<void>
  deadline: number
  pollMs: number
  retryLimit: number
  retryBackoffMs: number
}

export async function enableGitHubPrAutoMergeAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const method = stringInput(inputs, 'method') ?? 'squash'
  const prNumber = numberInput(inputs, 'prNumber')
  const repositoryUrl = stringInput(inputs, 'repositoryUrl')
  if (!repositoryUrl || prNumber === undefined)
    return fail('invalid-input', "enable-github-pr-auto-merge requires 'repositoryUrl' and 'prNumber'")
  const repository = parseGitHubRepository(repositoryUrl)
  if (!repository || method !== 'squash')
    return fail(
      'config-error',
      method !== 'squash' ? `Unsupported merge method '${method}'` : 'Invalid GitHub repository URL',
    )

  const resources = currentRunnerResources()
  const now = resources?.githubPrChecksTiming?.now ?? Date.now
  const waitMs = resources?.githubPrChecksTiming?.autoMergeWaitMs ?? DEFAULT_WAIT_MS
  const timing: Timing = {
    now,
    delay: resources?.githubPrChecksTiming?.delay ?? delayWithSignal,
    deadline: now() + waitMs,
    pollMs: resources?.githubPrChecksTiming?.pollIntervalMs ?? DEFAULT_POLL_MS,
    retryLimit: resources?.githubPrTransientRetry?.limit ?? DEFAULT_RETRY_LIMIT,
    retryBackoffMs: resources?.githubPrTransientRetry?.backoffMs ?? DEFAULT_RETRY_BACKOFF_MS,
  }
  const gh = getGitHubPrGh()
  const steps: GitHubPrStep[] = []
  const record = (name: string, command: string, result: CommandResult) =>
    steps.push({
      name,
      command,
      exitCode: result.exitCode,
      output: combinedGhOutput(result),
      ...timeoutStepMetadata(result),
    })

  const version = await runBounded(gh, ['--version'], host, timing)
  record('gh-precheck', 'gh --version', version)
  if (host.signal.aborted) return fail('aborted', 'Cancelled while checking the gh CLI')
  if (version.exitCode !== 0)
    return fail(version.status === 'timeout' ? 'retry-safe' : 'config-error', 'gh CLI is unavailable')
  const auth = await runBounded(gh, ['auth', 'status'], host, timing)
  record('gh-precheck', 'gh auth status', auth)
  if (host.signal.aborted) return fail('aborted', 'Cancelled while checking GitHub authentication')
  if (auth.exitCode !== 0)
    return fail(auth.status === 'timeout' ? 'retry-safe' : 'config-error', 'gh CLI is not authenticated')

  const read = async (): Promise<{ view?: View; error?: string; aborted?: boolean }> => {
    const args = withGitHubRepository(['pr', 'view', String(prNumber), '--json', VIEW_FIELDS], repository)
    const result = await runReadWithDeadline(gh, args, host, timing)
    record('gh-pr-view', `pr view ${prNumber} --json ${VIEW_FIELDS}`, result)
    if (host.signal.aborted) return { error: 'PR read was cancelled', aborted: true }
    if (result.exitCode !== 0) return { error: combinedGhOutput(result) || 'Unable to read PR' }
    return parseView(result.stdout)
  }

  const initial = await read()
  if (!initial.view) return fail(initial.aborted ? 'aborted' : 'retry-safe', initial.error ?? 'Unable to read PR')
  const terminal = terminalFailure(initial.view, prNumber)
  if (terminal) return terminal
  if (initial.view.state === 'MERGED') return success(false, initial.view)

  let enabled = false
  if (!initial.view.autoMergeRequest) {
    const subject = stringInput(inputs, 'subject') ?? initial.view.title
    const args = withGitHubRepository(
      ['pr', 'merge', String(prNumber), '--auto', '--squash', '--subject', subject, '--body', ''],
      repository,
    )
    const result = await runBounded(gh, args, host, timing)
    record('gh-pr-auto-merge', `pr merge ${prNumber} --auto --squash`, result)
    if (host.signal.aborted) return fail('aborted', `Cancelled while enabling auto-merge for PR #${prNumber}`)
    if (result.exitCode !== 0) {
      const output = combinedGhOutput(result)
      if (isUnavailable(output))
        return fail('auto-merge-unavailable', `Failed to enable auto-merge for PR #${prNumber}: ${output}`)
      if (!isAmbiguousRegistrationFailure(result, output)) {
        return fail(
          'enable-failed',
          `Failed to enable auto-merge for PR #${prNumber}: ${output || `exit ${result.exitCode}`}`,
        )
      }
      const reread = await read()
      if (!reread.view)
        return fail(
          reread.aborted ? 'aborted' : 'retry-safe',
          `Auto-merge registration was ambiguous and PR state could not be re-read: ${reread.error ?? output}`,
        )
      const rereadTerminal = terminalFailure(reread.view, prNumber)
      if (rereadTerminal) return rereadTerminal
      if (reread.view.state === 'MERGED') return success(false, reread.view)
      if (!reread.view.autoMergeRequest) {
        return fail(
          'retry-safe',
          `Auto-merge registration did not complete for PR #${prNumber}: ${output || 'ambiguous failure'}`,
        )
      }
    } else {
      enabled = true
    }
  }

  for (;;) {
    if (host.signal.aborted) return fail('aborted', `Cancelled while waiting for PR #${prNumber} to merge`)
    if (remainingMs(timing) <= 0) return fail('retry-safe', `Timed out waiting for PR #${prNumber} to merge`)
    const current = await read()
    if (!current.view) return fail(current.aborted ? 'aborted' : 'retry-safe', current.error ?? 'Unable to read PR')
    const view = current.view
    if (view.state === 'MERGED') return success(enabled, view)
    const failure = terminalFailure(view, prNumber)
    if (failure) return failure
    const parsed = parsePrStatusCheckRollupResult(JSON.stringify({ statusCheckRollup: view.statusCheckRollup }))
    if (parsed.kind === 'invalid') return fail('retry-safe', parsed.message)
    const checks = classifyPrChecks(parsed.checks)
    if (checks.kind === 'failed') return fail('pr-checks-failed', `PR #${prNumber} checks failed: ${checks.message}`)
    try {
      await timing.delay(Math.min(timing.pollMs, remainingMs(timing)), host.signal)
    } catch (error) {
      if (!host.signal.aborted) throw error
      return fail('aborted', `Cancelled while waiting for PR #${prNumber} to merge`)
    }
  }

  function success(wasEnabled: boolean, view: View): ActionResult {
    return succeed({
      kind: 'enable-github-pr-auto-merge',
      status: 'completed',
      prNumber: prNumber!,
      prUrl: view.url || null,
      method: 'squash',
      enabled: wasEnabled,
      mergeCommitSha: view.mergeCommit?.oid ?? null,
      output: steps
        .map((step) => step.output)
        .filter(Boolean)
        .join('\n'),
      steps: steps as unknown as JsonObject,
    })
  }
}

async function runBounded(
  gh: RunnerCommandRunner,
  args: string[],
  host: ActionHost,
  timing: Timing,
): Promise<CommandResult> {
  const remaining = remainingMs(timing)
  if (remaining <= 0)
    return { exitCode: 1, stdout: '', stderr: 'overall deadline exceeded', status: 'timeout', timeoutMs: 0 }
  const options: CommandLineOptions = {
    timeoutMs: Math.min(NETWORK_COMMAND_TIMEOUT_MS, remaining),
    ...(host.log ? { onLine: (line) => host.log!.write(ACTION_SOURCE, line) } : {}),
  }
  try {
    return await gh('gh', args, host.workDir, host.signal, undefined, options)
  } catch (error) {
    if (!host.signal.aborted || !isAbortRejection(error, host.signal)) throw error
    return {
      exitCode: 1,
      stdout: '',
      stderr: host.signal.reason instanceof Error ? host.signal.reason.message : 'cancelled',
    }
  }
}

async function runReadWithDeadline(
  gh: RunnerCommandRunner,
  args: string[],
  host: ActionHost,
  timing: Timing,
): Promise<CommandResult> {
  for (let attempt = 0; ; attempt++) {
    const result = await runBounded(gh, args, host, timing)
    const retry =
      result.exitCode !== 0 &&
      result.status !== 'timeout' &&
      attempt < timing.retryLimit &&
      looksLikeRetrySafe(`${result.stdout}\n${result.stderr}`)
    if (!retry) return result
    const delay = Math.min(timing.retryBackoffMs, remainingMs(timing))
    if (delay <= 0) return { ...result, status: 'timeout', timeoutMs: 0 }
    try {
      await timing.delay(delay, host.signal)
    } catch (error) {
      if (!host.signal.aborted) throw error
      return result
    }
  }
}

function isAbortRejection(error: unknown, signal: AbortSignal): boolean {
  if (error === signal.reason) return true
  if (!error || typeof error !== 'object') return false
  const value = error as { name?: unknown; code?: unknown }
  return value.name === 'AbortError' || value.code === 'ABORT_ERR'
}

function parseView(stdout: string): { view?: View; error?: string } {
  let value: unknown
  try {
    value = JSON.parse(stdout)
  } catch {
    return { error: 'GitHub returned invalid PR JSON' }
  }
  if (!value || typeof value !== 'object' || Array.isArray(value))
    return { error: 'GitHub returned invalid PR JSON shape' }
  const obj = value as Record<string, unknown>
  if (
    !['OPEN', 'CLOSED', 'MERGED'].includes(String(obj.state)) ||
    typeof obj.url !== 'string' ||
    typeof obj.title !== 'string' ||
    typeof obj.mergeStateStatus !== 'string' ||
    !Array.isArray(obj.statusCheckRollup) ||
    !(obj.autoMergeRequest === null || isRecord(obj.autoMergeRequest)) ||
    !(obj.mergeCommit === null || (isRecord(obj.mergeCommit) && typeof obj.mergeCommit.oid === 'string'))
  ) {
    return { error: 'GitHub returned unexpected PR JSON field shapes' }
  }
  return { view: obj as View }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value)
}

function terminalFailure(view: View, prNumber: number): ActionResult | null {
  if (view.state === 'CLOSED') return fail('pr-state-conflict', `PR #${prNumber} is closed`)
  if (view.mergeStateStatus === 'DIRTY') return fail('conflict', `PR #${prNumber} has merge conflicts`)
  return null
}

function isAmbiguousRegistrationFailure(result: CommandResult, output: string): boolean {
  return (
    result.status === 'timeout' ||
    !output.trim() ||
    looksLikeRetrySafe(output) ||
    /graphql|mutation|response.*lost/i.test(output)
  )
}

function isUnavailable(output: string): boolean {
  const lower = output.toLowerCase()
  return lower.includes('auto-merge') && (lower.includes('not enabled') || lower.includes('not allowed'))
}

function remainingMs(timing: Timing): number {
  return Math.max(0, timing.deadline - timing.now())
}
