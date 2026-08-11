import { execFileSync, spawn } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'
import { existsSync, lstatSync, readFileSync, readdirSync, statSync, unlinkSync, writeFileSync } from 'node:fs'
import { basename, dirname, relative, resolve, sep } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { evaluateTrack } from './budget.js'
import { parseSuiteConfig, validateConfig } from './config.js'
import { formatEvaluation, formatSummary, formatTrackRun, summarize } from './diagnostics.js'
import { runWithDeadline } from './deadline.js'
import {
  buildLedgerEnvironment,
  createExecutionRunId,
  manifestFromDiscovery,
  parseExecutionLedger,
  parseExecutionProvenance,
  readCurrentExecutionIdentity,
  serializeExecutionProvenance,
  validateCurrentExecutionIdentity,
  validateExecutionEvidence,
} from './execution-ledger.js'
import { parseReport } from './reports.js'
import { parseAssemblyName, resolveApphostPath, resolveDiscoveryCommand, resolveFocusedCommand } from './focused.js'
import type { CurrentExecutionIdentity, ExecutionLedgerExpectation, SuiteConfig, TrackConfig, TrackEvaluation, TrackRun } from './types.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

function readCsproj(csprojPath: string): string {
  return readFileSync(resolve(repoRoot, csprojPath), 'utf8')
}

function apphostFor(track: TrackConfig): string {
  if (track.apphost) return resolve(repoRoot, track.apphost)
  if (!track.csproj) throw new Error(`track "${track.id}": dotnet-apphost needs csproj or apphost`)
  const csprojAbs = resolve(repoRoot, track.csproj)
  const csprojDir = dirname(csprojAbs)
  const xml = readCsproj(track.csproj)
  // MSBuild defaults AssemblyName to the project file name when omitted.
  const assemblyName =
    track.apphost ? undefined : parseAssemblyName(xml) ?? basename(csprojAbs).replace(/\.csproj$/, '')
  return resolve(csprojDir, resolveApphostPath({ csprojXml: xml, projectDir: csprojDir, assemblyName }))
}

function sha256File(path: string): string {
  return createHash('sha256').update(readFileSync(path)).digest('hex')
}

const sourceDigestIgnoredDirectories = new Set(['.git', 'bin', 'node_modules', 'obj', 'reports'])

function sourceFiles(root: string): readonly string[] {
  const absoluteRoot = resolve(repoRoot, root)
  const relativeRoot = relative(repoRoot, absoluteRoot)
  if (relativeRoot === '..' || relativeRoot.startsWith(`..${sep}`)) {
    throw new Error(`execution source root escapes the repository: ${root}`)
  }

  const visit = (path: string): string[] => {
    const stat = lstatSync(path)
    if (stat.isSymbolicLink()) throw new Error(`execution source identity does not follow symbolic links: ${path}`)
    if (stat.isFile()) return [path]
    if (!stat.isDirectory()) throw new Error(`execution source identity only supports files and directories: ${path}`)
    return readdirSync(path, { withFileTypes: true })
      .filter((entry) => !entry.isDirectory() || !sourceDigestIgnoredDirectories.has(entry.name))
      .flatMap((entry) => visit(resolve(path, entry.name)))
  }

  return visit(absoluteRoot)
}

function sha256Sources(roots: readonly string[]): string {
  const files = [...new Set(roots.flatMap(sourceFiles))]
    .sort((left, right) => (left < right ? -1 : left > right ? 1 : 0))
  if (files.length === 0) throw new Error('execution source roots contain no files')

  const hash = createHash('sha256')
  for (const path of files) {
    hash.update(relative(repoRoot, path).split(sep).join('/'), 'utf8')
    hash.update('\0')
    hash.update(readFileSync(path))
    hash.update('\0')
  }
  return hash.digest('hex')
}

function assemblyPathFor(apphost: string): string {
  return apphost.endsWith('.exe') ? `${apphost.slice(0, -4)}.dll` : `${apphost}.dll`
}

function parallelismFor(track: TrackConfig): string {
  const args = track.apphostArgs ?? []
  const index = args.indexOf('-parallel')
  return index >= 0 && args[index + 1] ? `xunit-${args[index + 1]}` : 'xunit-default'
}

function executionLedgerPlan(track: TrackConfig): {
  readonly assemblyPath: string
  readonly discovery: { readonly apphost: string; readonly args: readonly string[] }
} {
  if (!track.executionLedger || !track.executionProvenance || track.kind !== 'dotnet-apphost' || !track.csproj) {
    throw new Error(`track "${track.id}" requires a dotnet-apphost csproj for execution ledger evidence`)
  }
  const csprojAbs = resolve(repoRoot, track.csproj)
  const projectDir = dirname(csprojAbs)
  const xml = readCsproj(track.csproj)
  const assemblyName = parseAssemblyName(xml) ?? basename(csprojAbs).replace(/\.csproj$/, '')
  const apphost = resolve(projectDir, resolveApphostPath({ csprojXml: xml, projectDir, assemblyName }))
  return {
    assemblyPath: assemblyPathFor(apphost),
    discovery: resolveDiscoveryCommand({ csprojXml: xml, projectDir, assemblyName }),
  }
}

export function commandFor(track: TrackConfig): { command: string; args: readonly string[] } {
  if (track.kind === 'dotnet-apphost') {
    const apphost = apphostFor(track)
    return {
      command: apphost,
      args: ['-noColor', '-noLogo', '-trx', resolve(repoRoot, track.report), ...(track.apphostArgs ?? [])],
    }
  }
  if (track.kind === 'dotnet-vstest') {
    if (!track.csproj) throw new Error(`track "${track.id}": dotnet-vstest needs csproj`)
    const reportDir = resolve(repoRoot, dirname(track.report))
    const logName = `${track.id}.trx`
    return {
      command: 'dotnet',
      args: [
        'test', resolve(repoRoot, track.csproj),
        '--no-build', '--no-restore',
        '--logger', `trx;LogFileName=${logName}`,
        '--results-directory', reportDir,
      ],
    }
  }
  if (track.run && track.run.length > 0) {
    // `{report}` resolves to an absolute report path so workspace-relative
    // tools (vitest runs with cwd = package dir) write where the guard reads.
    const reportAbs = resolve(repoRoot, track.report)
    const args = track.run.slice(1).map((arg) => arg.replace('{report}', reportAbs))
    return { command: track.run[0], args }
  }
  throw new Error(`track "${track.id}": no run command`)
}

function signalTree(pid: number, signal: NodeJS.Signals, graceMs: number): void {
  if (pid <= 1) throw new Error(`cannot signal invalid process tree pid ${pid}`)
  if (process.platform === 'win32') {
    const force = signal === 'SIGKILL' ? ['/F'] : []
    execFileSync('taskkill', ['/pid', String(pid), '/T', ...force], { stdio: 'ignore', timeout: graceMs })
  } else {
    process.kill(-pid, signal)
  }
}

export interface SpawnedChild {
  readonly done: Promise<{ exitCode: number | null; stdout: string }>
  readonly pid: number
}

export interface TimeoutHandle {
  readonly promise: Promise<void>
  readonly cancel: () => void
}

export interface TimeoutScheduler {
  readonly set: (callback: () => void, delayMs: number) => unknown
  readonly clear: (timer: unknown) => void
}

const nativeTimeoutScheduler: TimeoutScheduler = {
  set: (callback, delayMs) => setTimeout(callback, delayMs),
  clear: (timer) => clearTimeout(timer as ReturnType<typeof setTimeout>),
}

export function createTimeout(delayMs: number, scheduler: TimeoutScheduler = nativeTimeoutScheduler): TimeoutHandle {
  let timer: unknown
  const promise = new Promise<void>((resolvePromise) => {
    timer = scheduler.set(() => resolvePromise(), delayMs)
  })
  return { promise, cancel: () => scheduler.clear(timer) }
}

function spawnChild(
  command: string,
  args: readonly string[],
  extraEnvironment?: Readonly<Record<string, string>>,
  captureStdout = false,
): SpawnedChild {
  const detached = process.platform !== 'win32'
  const child = spawn(command, args as string[], {
    cwd: repoRoot,
    stdio: captureStdout ? ['ignore', 'pipe', 'inherit'] : 'inherit',
    detached,
    env: extraEnvironment ? { ...process.env, ...extraEnvironment } : undefined,
  })
  let stdout = ''
  child.stdout?.setEncoding('utf8')
  child.stdout?.on('data', (chunk: string) => { stdout += chunk })
  const done = new Promise<{ exitCode: number | null; stdout: string }>((resolvePromise) => {
    child.on('exit', (code) => resolvePromise({ exitCode: code, stdout }))
    child.on('error', () => resolvePromise({ exitCode: 1, stdout }))
  })
  return { done, pid: child.pid ?? -1 }
}

export async function runProcessWithDeadline<TimeoutReason>(input: {
  readonly child: SpawnedChild
  readonly timeout: Promise<TimeoutReason>
  readonly kill: () => Promise<void>
  readonly now: () => number
}): Promise<Awaited<SpawnedChild['done']> & {
  readonly status: 'passed' | 'failed' | 'timeout' | 'cleanup-failed'
  readonly elapsedMs: number
  readonly timeoutReason?: TimeoutReason
  readonly cleanupError?: string
}> {
  const outcome = await runWithDeadline({
    start: () => input.child.done,
    kill: input.kill,
    timeout: input.timeout,
    now: input.now,
  })
  if (outcome.status === 'timeout' || outcome.status === 'cleanup-failed') {
    return { ...outcome, stdout: '' }
  }
  const completed = await input.child.done
  return { ...outcome, stdout: completed.stdout }
}

export function reportFileReady(reportPath: string): boolean {
  return existsSync(reportPath) && statSync(reportPath).isFile()
}

function failedRun(track: TrackConfig, command: string, reportError: string): TrackRun {
  return {
    trackId: track.id,
    timedOut: false,
    exitCode: 1,
    elapsedMs: 0,
    deadlineMs: track.deadlineMs,
    command,
    reportReady: false,
    reportError,
  }
}

function suiteTimeoutRun(track: TrackConfig): TrackRun {
  return {
    trackId: track.id,
    timedOut: true,
    timeoutReason: 'suite',
    exitCode: null,
    elapsedMs: 0,
    deadlineMs: track.deadlineMs,
    command: 'not started: suite deadline',
    reportReady: false,
    reportError: `report ${track.report} was not refreshed because the suite deadline expired`,
  }
}

function cleanupBlockedRun(track: TrackConfig, blocker: string): TrackRun {
  return {
    ...failedRun(track, 'not started: prior child cleanup failed', `track was not started because ${blocker}`),
    cleanupFailed: true,
    cleanupError: blocker,
  }
}

export interface ProcessCleanupLifecycle {
  readonly signal: (pid: number, signal: NodeJS.Signals) => void
  readonly createGrace: () => TimeoutHandle
}

async function waitForExit(child: SpawnedChild, grace: TimeoutHandle): Promise<boolean> {
  try {
    return await Promise.race([
      child.done.then(() => true),
      grace.promise.then(() => false),
    ])
  } finally {
    grace.cancel()
  }
}

export async function terminateChildTree(
  child: SpawnedChild,
  lifecycle: ProcessCleanupLifecycle,
): Promise<void> {
  const errors: string[] = []
  let exited = false
  try {
    lifecycle.signal(child.pid, 'SIGTERM')
    exited = await waitForExit(child, lifecycle.createGrace())
  } catch (error) {
    errors.push(`SIGTERM failed: ${(error as Error).message}`)
  }

  if (!exited) {
    try {
      lifecycle.signal(child.pid, 'SIGKILL')
    } catch (error) {
      errors.push(`SIGKILL failed: ${(error as Error).message}`)
    }
    exited = await waitForExit(child, lifecycle.createGrace())
  }

  if (!exited) errors.push('child process tree termination was not confirmed within cleanup grace')
  if (errors.length > 0) throw new Error(errors.join('; '))
}

function killTree(child: SpawnedChild, graceMs: number): Promise<void> {
  return terminateChildTree(child, {
    signal: (pid, signal) => signalTree(pid, signal, graceMs),
    createGrace: () => createTimeout(graceMs),
  })
}

class GateProcessError extends Error {
  constructor(
    message: string,
    readonly status: 'failed' | 'timeout' | 'cleanup-failed',
    readonly timeoutReason?: 'track' | 'suite',
    readonly cleanupError?: string,
  ) {
    super(message)
  }
}

async function readTrackCurrentIdentity(
  track: TrackConfig,
  graceMs: number,
  deadline: Promise<'track' | 'suite'>,
): Promise<CurrentExecutionIdentity> {
  const plan = executionLedgerPlan(track)
  if (!track.executionSourceRoots || track.executionSourceRoots.length === 0) {
    throw new Error(`track "${track.id}" requires executionSourceRoots for execution ledger evidence`)
  }
  return readCurrentExecutionIdentity({
    assemblyPath: plan.assemblyPath,
    sourceRoots: track.executionSourceRoots,
    parallelism: parallelismFor(track),
  }, {
    readAssemblySha256: sha256File,
    readSourceSha256: sha256Sources,
    readDiscovery: async () => {
      const child = spawnChild(plan.discovery.apphost, plan.discovery.args, undefined, true)
      const result = await runProcessWithDeadline({
        child,
        timeout: deadline,
        kill: () => killTree(child, graceMs),
        now: () => Date.now(),
      })
      if (result.status !== 'passed') {
        const message = result.cleanupError
          ? `compiled discovery cleanup failed: ${result.cleanupError}`
          : result.status === 'timeout'
            ? 'compiled discovery exceeded the track or suite deadline'
            : `compiled discovery failed with exit ${result.exitCode}`
        throw new GateProcessError(message, result.status, result.timeoutReason, result.cleanupError)
      }
      return result.stdout
    },
  })
}

async function runTrack(track: TrackConfig, graceMs: number, suiteDeadline: Promise<void>): Promise<TrackRun> {
  const reportPath = resolve(repoRoot, track.report)
  const ledgerPath = track.executionLedger ? resolve(repoRoot, track.executionLedger) : undefined
  const provenancePath = track.executionProvenance ? resolve(repoRoot, track.executionProvenance) : undefined
  const trackStartedAt = Date.now()
  const trackDeadline = createTimeout(track.deadlineMs)
  const deadline = Promise.race([
    trackDeadline.promise.then(() => 'track' as const),
    suiteDeadline.then(() => 'suite' as const),
  ])
  try {
    let command: string
    let args: readonly string[]
    let ledgerExpectation: ExecutionLedgerExpectation | undefined
    let ledgerEnvironment: Readonly<Record<string, string>> | undefined
    try {
      const artifactPaths = [reportPath, ledgerPath, provenancePath].filter((path): path is string => path !== undefined)
      if (new Set(artifactPaths).size !== artifactPaths.length) {
        throw new Error('TRX report, execution ledger, and execution provenance paths must differ')
      }
      for (const artifactPath of artifactPaths) {
        try {
          unlinkSync(artifactPath)
        } catch (error) {
          if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
        }
      }
      if (ledgerPath) {
        if (!provenancePath) throw new Error('execution provenance path is required')
        let currentIdentity: CurrentExecutionIdentity
        try {
          currentIdentity = await readTrackCurrentIdentity(track, graceMs, deadline)
        } catch (error) {
          if (!(error instanceof GateProcessError)) throw error
          return {
            ...failedRun(track, 'compiled discovery', error.message),
            timedOut: error.status !== 'failed',
            timeoutReason: error.timeoutReason,
            exitCode: error.status === 'failed' ? 1 : null,
            elapsedMs: Date.now() - trackStartedAt,
            executionLedgerReady: false,
            executionLedgerError: error.message,
            cleanupFailed: error.status === 'cleanup-failed',
            cleanupError: error.cleanupError,
          }
        }
        ledgerExpectation = {
          runId: createExecutionRunId({ now: () => Date.now() }, randomUUID),
          ...currentIdentity,
        }
        writeFileSync(provenancePath, serializeExecutionProvenance(ledgerExpectation), 'utf8')
        ledgerEnvironment = buildLedgerEnvironment({ ...ledgerExpectation, ledgerPath })
      }
      ({ command, args } = commandFor(track))
    } catch (error) {
      const failed = failedRun(track, 'not started', `could not prepare report ${track.report}: ${(error as Error).message}`)
      return ledgerPath
        ? { ...failed, elapsedMs: Date.now() - trackStartedAt, executionLedgerReady: false, executionLedgerError: (error as Error).message }
        : { ...failed, elapsedMs: Date.now() - trackStartedAt }
    }

    const cmdString = `${command} ${args.join(' ')}`
    let child: SpawnedChild
    try {
      child = spawnChild(command, args, ledgerEnvironment)
    } catch (error) {
      const failed = failedRun(track, cmdString, `could not start track: ${(error as Error).message}`)
      return ledgerPath
        ? { ...failed, elapsedMs: Date.now() - trackStartedAt, executionLedgerReady: false, executionLedgerError: (error as Error).message }
        : { ...failed, elapsedMs: Date.now() - trackStartedAt }
    }
    const result = await runProcessWithDeadline({
      child,
      timeout: deadline,
      kill: () => killTree(child, graceMs),
      now: () => Date.now(),
    })
    let ready = false
    try {
      ready = reportFileReady(reportPath)
    } catch {
      ready = false
    }
    let executionLedgerReady: boolean | undefined
    let executionLedgerError: string | undefined
    if (ledgerPath) {
      try {
        executionLedgerReady = existsSync(ledgerPath) && statSync(ledgerPath).isFile()
      } catch {
        executionLedgerReady = false
      }
      if (!executionLedgerReady) executionLedgerError = `execution ledger ${track.executionLedger} was not created or refreshed by the track`
    }
    return {
      trackId: track.id,
      timedOut: result.status === 'timeout',
      timeoutReason: result.timeoutReason,
      exitCode: result.exitCode,
      elapsedMs: Date.now() - trackStartedAt,
      deadlineMs: track.deadlineMs,
      command: cmdString,
      reportReady: ready,
      reportError: ready ? undefined : `report ${track.report} was not created or refreshed by the track`,
      executionLedgerReady,
      executionLedgerError,
      executionLedgerExpectation: ledgerExpectation,
      cleanupFailed: result.status === 'cleanup-failed',
      cleanupError: result.cleanupError,
    }
  } finally {
    trackDeadline.cancel()
  }
}

function failedEvaluation(track: TrackConfig, reportError: string): TrackEvaluation {
  return {
    trackId: track.id,
    enforce: track.enforce,
    status: track.status,
    reason: track.reason,
    reportError,
    total: 0,
    failedTests: [],
    rules: [],
    passed: false,
  }
}

export interface TrackArtifactReader {
  readonly readText: (path: string) => string
}

export function evaluateTrackArtifacts(
  track: TrackConfig,
  artifacts: TrackArtifactReader,
  run?: TrackRun,
  today: Date = new Date(),
  currentIdentity?: CurrentExecutionIdentity,
): TrackEvaluation {
  if (run && !run.reportReady) {
    return failedEvaluation(track, run.reportError ?? `report ${track.report} was not refreshed`)
  }
  if (track.executionLedger && run && !run.executionLedgerReady) {
    return failedEvaluation(track, run.executionLedgerError ?? `execution ledger ${track.executionLedger} was not refreshed`)
  }
  try {
    const trxContent = artifacts.readText(track.report)
    const trxCases = parseReport(track.reportFormat, trxContent)
    if (track.executionLedger) {
      if (!track.executionProvenance) return failedEvaluation(track, 'execution provenance path is not configured')
      const expected = parseExecutionProvenance(artifacts.readText(track.executionProvenance))
      if (run?.executionLedgerExpectation) {
        if (serializeExecutionProvenance(run.executionLedgerExpectation) !== serializeExecutionProvenance(expected)) {
          return failedEvaluation(track, 'saved execution provenance does not match the current run')
        }
      } else {
        if (!currentIdentity) return failedEvaluation(track, 'current execution identity was not captured for saved evidence')
        const identityErrors = validateCurrentExecutionIdentity(expected, currentIdentity)
        if (identityErrors.length > 0) {
          return failedEvaluation(track, `saved execution provenance is stale: ${identityErrors.join('; ')}`)
        }
      }
      const ledgerContent = artifacts.readText(track.executionLedger)
      const parsedLedger = parseExecutionLedger(ledgerContent)
      const evidence = validateExecutionEvidence(trxCases, parsedLedger, expected)
      if (evidence.errors.length > 0) {
        return failedEvaluation(track, `execution ledger contract failed: ${evidence.errors.join('; ')}`)
      }
      return evaluateTrack(track, evidence.cases, today)
    }
    return evaluateTrack(track, trxCases, today)
  } catch (error) {
    return failedEvaluation(track, `could not read report ${track.report}: ${(error as Error).message}`)
  }
}

function evaluateFromFile(track: TrackConfig, run?: TrackRun): TrackEvaluation {
  return evaluateTrackArtifacts(track, {
    readText: (path) => readFileSync(resolve(repoRoot, path), 'utf8'),
  }, run)
}

function focusedFlow(csprojPath: string, className: string): number {
  try {
    const xml = readCsproj(csprojPath)
    const assemblyName = parseAssemblyName(xml) ?? basename(csprojPath).replace(/\.csproj$/, '')
    const cmd = resolveFocusedCommand({ csprojXml: xml, className, projectDir: dirname(csprojPath), assemblyName })
    const list = execFileSync(cmd.apphost, cmd.verify as string[], { cwd: repoRoot, encoding: 'utf8' })
    const classes = list.split('\n').map((line) => line.trim()).filter(Boolean)
    if (!classes.includes(className)) {
      const suggestion = classes.find((c) => c.endsWith(`.${className}`) || c.includes(className))
      process.stderr.write(
        `class not found: ${className}\n` +
          (suggestion ? `did you mean: ${suggestion}\n` : `available classes written above\n`),
      )
      return 2
    }
    console.log(`# focused run (apphost -class, never dotnet --filter)\n${cmd.apphost} ${cmd.args.join(' ')}`)
    try {
      execFileSync(cmd.apphost, cmd.args as string[], { cwd: repoRoot, stdio: 'inherit' })
      return 0
    } catch {
      return 1
    }
  } catch (error) {
    // Bad input (missing csproj/apphost, unreadable csproj) is a usage error:
    // fail explicitly instead of surfacing an unhandled exception.
    process.stderr.write(`focused run failed: ${(error as Error).message}\n`)
    return 2
  }
}

interface Args {
  mode: 'run' | 'check' | 'focused'
  tracks: string[]
  all: boolean
  focused?: { csproj: string; className: string }
}

export function parseArgs(argv: readonly string[]): Args {
  const tracks: string[] = []
  let mode: 'run' | 'check' | 'focused' = 'run'
  let all = false
  let focused: { csproj: string; className: string } | undefined
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--check') mode = 'check'
    else if (arg === '--all') all = true
    else if (arg === '--track') tracks.push(argv[++i])
    else if (arg.startsWith('--track=')) tracks.push(arg.slice('--track='.length))
    else if (arg === 'focused') {
      mode = 'focused'
      const csproj = argv[i + 1]
      const className = argv[i + 2]
      // Missing or partial focused args must not reach resolve(undefined);
      // main() turns an absent request into usage + exit 2.
      if (!csproj || !className) {
        focused = undefined
      } else {
        focused = { csproj, className }
        i += 2
      }
    }
  }
  return { mode, tracks, all, focused }
}

export async function main(argv: readonly string[] = process.argv.slice(2)): Promise<number> {
  const { mode, tracks, all, focused } = parseArgs(argv)

  if (mode === 'focused') {
    if (!focused) {
      process.stderr.write('usage: guard focused <csproj> <ClassName.FQN>\n')
      return 2
    }
    return focusedFlow(resolve(repoRoot, focused.csproj), focused.className)
  }

  const configText = readFileSync(resolve(repoRoot, 'test-duration.config.jsonc'), 'utf8')
  const config = parseSuiteConfig(configText)
  const errors = validateConfig(config)
  if (errors.length > 0) {
    process.stderr.write(`invalid test-duration config:\n${errors.map((e) => `  - ${e}`).join('\n')}\n`)
    return 2
  }

  const graceMs = config.killGraceMs ?? 5000
  let selected: readonly TrackConfig[]
  if (tracks.length > 0) {
    selected = config.tracks.filter((t) => tracks.includes(t.id))
  } else {
    // Default gate: enforced tracks only (fast, green). --all adds the
    // deadline-governed, baseline-pending tracks for full coverage.
    selected = all ? config.tracks : config.tracks.filter((t) => t.enforce)
  }
  if (selected.length === 0) {
    process.stderr.write(`no tracks matched: ${tracks.join(', ')}\n`)
    return 2
  }

  const runs: TrackRun[] = []
  const evaluations: TrackEvaluation[] = []

  if (mode === 'run') {
    const suiteStart = Date.now()
    let suiteExpired = false
    const suiteTimer = createTimeout(config.suiteDeadlineMs)
    const suiteDeadline = suiteTimer.promise.then(() => {
      suiteExpired = true
    })
    try {
      for (let i = 0; i < selected.length; i++) {
        const track = selected[i]
        if (suiteExpired) {
          runs.push(suiteTimeoutRun(track))
          continue
        }
        const run = await runTrack(track, graceMs, suiteDeadline)
        runs.push(run)
        if (run.timeoutReason === 'track') console.error(`  ${track.id}: exceeded ${track.deadlineMs}ms deadline`)
        if (run.cleanupFailed) {
          for (const blocked of selected.slice(i + 1)) {
            runs.push(cleanupBlockedRun(blocked, run.cleanupError ?? `${track.id} cleanup failed`))
          }
          break
        }
        if (suiteExpired) {
          for (const skipped of selected.slice(i + 1)) runs.push(suiteTimeoutRun(skipped))
          break
        }
      }
    } finally {
      suiteTimer.cancel()
    }
    const suiteElapsed = Date.now() - suiteStart
    const suiteDeadlineBreached = suiteExpired || suiteElapsed >= config.suiteDeadlineMs
    if (suiteDeadlineBreached) {
      console.error(`suite deadline breached after ${suiteElapsed}ms`)
    }

    const runsByTrack = new Map(runs.map((run) => [run.trackId, run]))
    for (const track of selected) {
      try {
        evaluations.push(evaluateFromFile(track, runsByTrack.get(track.id)))
      } catch (error) {
        evaluations.push(failedEvaluation(track, `could not evaluate report ${track.report}: ${(error as Error).message}`))
      }
    }

    console.log('runs:')
    for (const run of runs) console.log(formatTrackRun(run))
    console.log('budget:')
    for (const evaluation of evaluations) {
      for (const line of formatEvaluation(evaluation)) console.log(line)
    }
    const summary = summarize(runs, evaluations, suiteDeadlineBreached, suiteElapsed)
    console.log(formatSummary(summary, config.suiteDeadlineMs))
    const runFailed = suiteDeadlineBreached || runs.some((r) => r.timedOut || r.cleanupFailed || r.exitCode !== 0)
    const budgetFailed = evaluations.some((e) => !e.passed)
    return runFailed || budgetFailed ? 1 : 0
  }

  const checkSuiteTimer = createTimeout(config.suiteDeadlineMs)
  let checkSuiteExpired = false
  const checkSuiteDeadline = checkSuiteTimer.promise.then(() => {
    checkSuiteExpired = true
  })
  try {
    for (let i = 0; i < selected.length; i++) {
      const track = selected[i]
      if (checkSuiteExpired) {
        evaluations.push(failedEvaluation(track, 'suite deadline expired before current identity validation'))
        continue
      }
      let currentIdentity: CurrentExecutionIdentity | undefined
      let cleanupFailure: string | undefined
      if (track.executionLedger) {
        const trackTimer = createTimeout(track.deadlineMs)
        try {
          currentIdentity = await readTrackCurrentIdentity(track, graceMs, Promise.race([
            trackTimer.promise.then(() => 'track' as const),
            checkSuiteDeadline.then(() => 'suite' as const),
          ]))
        } catch (error) {
          evaluations.push(failedEvaluation(track, `could not validate current execution identity: ${(error as Error).message}`))
          if (error instanceof GateProcessError && error.status === 'cleanup-failed') cleanupFailure = error.message
        } finally {
          trackTimer.cancel()
        }
      }
      if (!evaluations.some((evaluation) => evaluation.trackId === track.id)) {
        evaluations.push(evaluateTrackArtifacts(track, {
          readText: (path) => readFileSync(resolve(repoRoot, path), 'utf8'),
        }, undefined, new Date(), currentIdentity))
      }
      if (cleanupFailure) {
        for (const blocked of selected.slice(i + 1)) {
          evaluations.push(failedEvaluation(blocked, `current identity validation stopped because ${cleanupFailure}`))
        }
        break
      }
    }
  } finally {
    checkSuiteTimer.cancel()
  }
  console.log('budget:')
  for (const evaluation of evaluations) {
    for (const line of formatEvaluation(evaluation)) console.log(line)
  }
  const summary = summarize(runs, evaluations, checkSuiteExpired)
  console.log(formatSummary(summary, config.suiteDeadlineMs))

  const runFailed = runs.some((r) => r.timedOut || r.exitCode !== 0)
  const budgetFailed = evaluations.some((e) => !e.passed)
  return runFailed || budgetFailed ? 1 : 0
}

const isMain = process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  void main().then((code) => process.exit(code), (error) => {
    console.error(`test-duration: fatal guard error: ${(error as Error).message}`)
    process.exit(1)
  })
}
