import { execFileSync, spawn } from 'node:child_process'
import { createWriteStream, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { finished } from 'node:stream/promises'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { createArtifactRoot as createUniqueArtifactRoot, type ArtifactDirectoryOps } from './artifacts.js'
import { externalAbortCleanupDeadlineAt, suiteDeadlines, type SuiteDeadlines } from './deadline.js'
import { main as runDurationGate, type GuardRuntime, type TimeoutScheduler } from './guard.js'
import { nativeProcessTreeOps, terminateProcessTree } from './process-tree.js'
import { resolveSpawnCommand } from './spawn-command.js'
import { nativeTimeSource } from './time.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')
const suiteDeadlineMs = 300_000
const killGraceMs = 5_000

export interface PhaseResult {
  readonly exitCode: number | null
  readonly timedOut: boolean
  readonly cancelled?: boolean
  readonly cleanupComplete?: boolean
}

export interface TerminationLease {
  readonly signal: AbortSignal
  readonly dispose: () => void
}

export interface SourceIdentity {
  readonly revision: string
  readonly changes: string
}

export interface CanonicalGateRuntime {
  readonly now: () => number
  readonly pid: () => number
  readonly sourceIdentity: () => SourceIdentity
  readonly createArtifactRoot: (runId: string, artifactParent?: string) => string
  readonly writeFile: (path: string, content: string) => void
  readonly runPhase: (
    name: string,
    command: string,
    args: readonly string[],
    artifactRoot: string,
    deadlines: SuiteDeadlines,
    now: () => number,
    abortSignal: AbortSignal,
    timeoutScheduler?: TimeoutScheduler,
  ) => Promise<PhaseResult>
  readonly runDurationGate: (argv: readonly string[], runtime: GuardRuntime) => Promise<number>
  readonly report: (line: string) => void
  readonly createTerminationSignal?: () => TerminationLease
  readonly timeoutScheduler?: TimeoutScheduler
}

export { type ArtifactDirectoryOps }

export function createArtifactRoot(
  runId: string,
  artifactParentOrOps?: string | ArtifactDirectoryOps,
  explicitOps?: ArtifactDirectoryOps,
): string {
  const artifactParent = typeof artifactParentOrOps === 'string' ? artifactParentOrOps : undefined
  const ops = typeof artifactParentOrOps === 'string' ? explicitOps : artifactParentOrOps
  return createUniqueArtifactRoot(runId, repoRoot, artifactParent, ops)
}

function createTimeout(
  deadlineAt: number,
  now: () => number,
  scheduler?: TimeoutScheduler,
): { readonly promise: Promise<void>; readonly cancel: () => void } {
  let timer: unknown
  const promise = new Promise<void>((resolvePromise) => {
    const delayMs = Math.max(0, deadlineAt - now())
    timer = scheduler === undefined ? setTimeout(resolvePromise, delayMs) : scheduler.set(resolvePromise, delayMs)
  })
  return {
    promise,
    cancel: () => {
      if (scheduler === undefined) clearTimeout(timer as ReturnType<typeof setTimeout>)
      else scheduler.clear(timer)
    },
  }
}

async function settleOutputBefore(
  output: Promise<unknown>,
  hardDeadlineAt: number,
  now: () => number,
  timeoutScheduler?: TimeoutScheduler,
): Promise<boolean> {
  if (now() >= hardDeadlineAt) return false
  const timeout = createTimeout(hardDeadlineAt, now, timeoutScheduler)
  try {
    const settled = await Promise.race([
      output.then(
        () => true,
        () => true,
      ),
      timeout.promise.then(() => false),
    ])
    return settled && now() < hardDeadlineAt
  } finally {
    timeout.cancel()
  }
}

function waitForAbort(signal: AbortSignal): { readonly promise: Promise<void>; readonly dispose: () => void } {
  if (signal.aborted) return { promise: Promise.resolve(), dispose: () => {} }
  let listener: (() => void) | undefined
  const promise = new Promise<void>((resolvePromise) => {
    listener = () => resolvePromise()
    signal.addEventListener('abort', listener, { once: true })
  })
  return {
    promise,
    dispose: () => {
      if (listener !== undefined) signal.removeEventListener('abort', listener)
    },
  }
}

function createNativeTerminationSignal(): TerminationLease {
  const controller = new AbortController()
  const abort = () => controller.abort()
  process.once('SIGTERM', abort)
  process.once('SIGINT', abort)
  return {
    signal: controller.signal,
    dispose: () => {
      process.removeListener('SIGTERM', abort)
      process.removeListener('SIGINT', abort)
    },
  }
}

export async function runPhase(
  name: string,
  command: string,
  args: readonly string[],
  artifactRoot: string,
  deadlines: SuiteDeadlines,
  now: () => number,
  abortSignal: AbortSignal,
  timeoutScheduler?: TimeoutScheduler,
): Promise<PhaseResult> {
  if (abortSignal.aborted) return { exitCode: null, timedOut: false, cancelled: true, cleanupComplete: true }
  if (now() >= deadlines.executionDeadlineAt) {
    return { exitCode: null, timedOut: true, cleanupComplete: true }
  }

  const stdoutPath = resolve(artifactRoot, 'logs', `${name}.stdout.log`)
  const stderrPath = resolve(artifactRoot, 'logs', `${name}.stderr.log`)
  mkdirSync(dirname(stdoutPath), { recursive: true })
  const stdout = createWriteStream(stdoutPath)
  const stderr = createWriteStream(stderrPath)
  const resolvedCommand = resolveSpawnCommand(command, args)
  const child = spawn(resolvedCommand.command, resolvedCommand.args as string[], {
    cwd: repoRoot,
    detached: process.platform !== 'win32',
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  child.stdout?.on('data', (chunk: Buffer) => {
    stdout.write(chunk)
    process.stdout.write(chunk)
  })
  child.stderr?.on('data', (chunk: Buffer) => {
    stderr.write(chunk)
    process.stderr.write(chunk)
  })

  let ended = false
  const endOutput = () => {
    if (ended) return
    ended = true
    stdout.end()
    stderr.end()
  }
  const exited = new Promise<number | null>((resolveExit) => {
    let settled = false
    const settle = (code: number | null) => {
      if (settled) return
      settled = true
      endOutput()
      resolveExit(code)
    }
    child.once('error', () => settle(1))
    child.once('close', (code) => settle(code))
  })
  const outputFinished = Promise.allSettled([finished(stdout), finished(stderr)])
  const executionDeadline = createTimeout(deadlines.executionDeadlineAt, now, timeoutScheduler)
  const aborted = waitForAbort(abortSignal)
  let outcome:
    | { readonly kind: 'exit'; readonly exitCode: number | null }
    | { readonly kind: 'deadline' }
    | { readonly kind: 'abort' }
  try {
    outcome = await Promise.race([
      exited.then((exitCode) => ({ kind: 'exit' as const, exitCode })),
      executionDeadline.promise.then(() => ({ kind: 'deadline' as const })),
      aborted.promise.then(() => ({ kind: 'abort' as const })),
    ])
  } finally {
    executionDeadline.cancel()
    aborted.dispose()
  }

  const cleanupDeadlineAt =
    outcome.kind === 'abort'
      ? externalAbortCleanupDeadlineAt(now(), deadlines.hardDeadlineAt, killGraceMs)
      : deadlines.hardDeadlineAt
  const cleanupComplete = await terminateProcessTree(
    { pid: child.pid ?? -1, done: exited },
    cleanupDeadlineAt,
    killGraceMs,
    { ...nativeProcessTreeOps, now },
  )
  endOutput()
  const outputComplete = await settleOutputBefore(outputFinished, cleanupDeadlineAt, now, timeoutScheduler)
  const executionExpired = now() >= deadlines.executionDeadlineAt
  return {
    exitCode: outcome.kind === 'exit' && !executionExpired ? outcome.exitCode : null,
    timedOut: outcome.kind === 'deadline' || executionExpired,
    cancelled: outcome.kind === 'abort',
    cleanupComplete: cleanupComplete && outputComplete,
  }
}

function nativeSourceIdentity(): SourceIdentity {
  return {
    revision: execFileSync('git', ['rev-parse', 'HEAD'], {
      cwd: repoRoot,
      encoding: 'utf8',
    }).trim(),
    changes: execFileSync('git', ['status', '--porcelain=v1', '--untracked-files=all'], {
      cwd: repoRoot,
      encoding: 'utf8',
    }).trim(),
  }
}

function assertMatchingCleanSource(expected: SourceIdentity, actual: SourceIdentity): void {
  if (actual.changes) {
    throw new Error('canonical gate requires a clean index and worktree')
  }
  if (actual.revision !== expected.revision) {
    throw new Error(`canonical source revision changed from ${expected.revision} to ${actual.revision}`)
  }
}

export function phaseSucceeded(result: PhaseResult): boolean {
  return result.exitCode === 0 && !result.timedOut && !result.cancelled && result.cleanupComplete !== false
}

function linkAbortSignal(parent: AbortSignal): {
  readonly signal: AbortSignal
  readonly dispose: () => void
  readonly abort: () => void
} {
  const controller = new AbortController()
  const abort = () => {
    if (!controller.signal.aborted) controller.abort()
  }
  if (parent.aborted) abort()
  else parent.addEventListener('abort', abort, { once: true })
  return {
    signal: controller.signal,
    abort,
    dispose: () => parent.removeEventListener('abort', abort),
  }
}

async function runBuildAndBoundaries(
  runtime: CanonicalGateRuntime,
  artifactRoot: string,
  deadlines: SuiteDeadlines,
  source: SourceIdentity,
  now: () => number,
  abortSignal: AbortSignal,
  timeoutScheduler?: TimeoutScheduler,
): Promise<{ readonly build: PhaseResult; readonly boundary: PhaseResult }> {
  const linked = linkAbortSignal(abortSignal)
  const run = (name: string, args: readonly string[], afterSuccess?: () => void): Promise<PhaseResult> =>
    runtime
      .runPhase(name, 'npm', args, artifactRoot, deadlines, now, linked.signal, timeoutScheduler)
      .then((result) => {
        if (!phaseSucceeded(result)) linked.abort()
        else afterSuccess?.()
        return result
      })
      .catch((error) => {
        linked.abort()
        throw error
      })

  try {
    const buildPromise = run('build', ['run', 'build'], () =>
      assertMatchingCleanSource(source, runtime.sourceIdentity()),
    )
    const boundaryPromise = run('script-boundaries', ['run', 'archtest'])
    const results = await Promise.allSettled([buildPromise, boundaryPromise])
    if (results[0].status === 'rejected') throw results[0].reason
    if (results[1].status === 'rejected') throw results[1].reason
    return {
      build: results[0].value,
      boundary: results[1].value,
    }
  } finally {
    linked.dispose()
  }
}

export interface CanonicalArgs {
  readonly artifactParent?: string
}

export function parseArgs(argv: readonly string[]): CanonicalArgs {
  let artifactParent: string | undefined
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index]
    if (arg === '--artifact-root') artifactParent = argv[++index]
    else if (arg.startsWith('--artifact-root=')) artifactParent = arg.slice('--artifact-root='.length)
    else throw new Error(`unknown canonical-gate argument: ${arg}`)
  }
  if (artifactParent === '') throw new Error('--artifact-root must not be empty')
  return { artifactParent }
}

const nativeRuntime: CanonicalGateRuntime = {
  now: nativeTimeSource.now,
  pid: () => process.pid,
  sourceIdentity: nativeSourceIdentity,
  createArtifactRoot,
  writeFile: (path, content) => writeFileSync(path, content),
  runPhase,
  runDurationGate,
  report: (line) => console.log(line),
  createTerminationSignal: createNativeTerminationSignal,
}

export async function main(
  runtime: CanonicalGateRuntime = nativeRuntime,
  argv: readonly string[] = process.argv.slice(2),
): Promise<number> {
  const { artifactParent } = parseArgs(argv)
  const termination = runtime.createTerminationSignal?.()
  const abortSignal = termination?.signal ?? new AbortController().signal
  let artifactRoot: string | undefined
  try {
    const startedAt = runtime.now()
    const runId = `${startedAt}-${runtime.pid()}`
    artifactRoot = runtime.createArtifactRoot(runId, artifactParent)
    runtime.report(`canonical-gate diagnostics: ${artifactRoot}`)
    const deadlines = suiteDeadlines(startedAt, suiteDeadlineMs, killGraceMs)
    const source = runtime.sourceIdentity()
    assertMatchingCleanSource(source, source)
    const sourceRevision = source.revision
    runtime.writeFile(
      resolve(artifactRoot, 'run.json'),
      JSON.stringify({ runId, startedAt, suiteDeadlineMs, sourceRevision }, null, 2) + '\n',
    )

    const docs = await runtime.runPhase(
      'docs',
      'npm',
      ['run', 'docs:check'],
      artifactRoot,
      deadlines,
      runtime.now,
      abortSignal,
      runtime.timeoutScheduler,
    )
    if (docs.timedOut || docs.cancelled || docs.cleanupComplete === false || docs.exitCode !== 0) return 1
    if (abortSignal.aborted || runtime.now() >= deadlines.executionDeadlineAt) return 1

    const { build, boundary } = await runBuildAndBoundaries(
      runtime,
      artifactRoot,
      deadlines,
      source,
      runtime.now,
      abortSignal,
      runtime.timeoutScheduler,
    )
    if (!phaseSucceeded(build) || !phaseSucceeded(boundary)) return 1
    if (abortSignal.aborted || runtime.now() >= deadlines.executionDeadlineAt) return 1
    assertMatchingCleanSource(source, runtime.sourceIdentity())

    runtime.writeFile(
      resolve(artifactRoot, 'build-stamp.json'),
      JSON.stringify({ runId, builtAt: runtime.now(), sourceRevision }, null, 2) + '\n',
    )

    const durationCode = await runtime.runDurationGate(
      [
        '--all',
        '--run-root',
        artifactRoot,
        '--require-build-stamp',
        '--require-enforced',
        '--suite-deadline-at-ms',
        String(deadlines.hardDeadlineAt),
      ],
      { now: runtime.now, abortSignal, timeoutScheduler: runtime.timeoutScheduler },
    )
    assertMatchingCleanSource(source, runtime.sourceIdentity())
    return durationCode === 0 && !abortSignal.aborted && runtime.now() < deadlines.hardDeadlineAt ? 0 : 1
  } catch (error) {
    if (artifactRoot !== undefined) {
      try {
        runtime.writeFile(
          resolve(artifactRoot, 'fatal-error.json'),
          JSON.stringify({ message: error instanceof Error ? error.message : String(error) }, null, 2) + '\n',
        )
      } catch {
        // The command remains fail-closed when the diagnostic sink itself fails.
      }
    }
    runtime.report(`canonical-gate fatal error: ${error instanceof Error ? error.message : String(error)}`)
    return 1
  } finally {
    termination?.dispose()
  }
}

const isMain = process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  void main().then(
    (code) => process.exit(code),
    (error) => {
      console.error(`canonical-gate: fatal error: ${(error as Error).message}`)
      process.exit(1)
    },
  )
}
