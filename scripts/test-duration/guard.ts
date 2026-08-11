import { execFileSync, spawn } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'
import { existsSync, readFileSync, statSync, unlinkSync } from 'node:fs'
import { basename, dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { evaluateTrack } from './budget.js'
import { parseSuiteConfig, validateConfig } from './config.js'
import { formatEvaluation, formatSummary, formatTrackRun, summarize } from './diagnostics.js'
import { runWithDeadline } from './deadline.js'
import {
  buildLedgerEnvironment,
  createExecutionRunId,
  discoverManifest,
  parseExecutionLedger,
  validateExecutionEvidence,
} from './execution-ledger.js'
import { parseReport } from './reports.js'
import { parseAssemblyName, resolveApphostPath, resolveDiscoveryCommand, resolveFocusedCommand } from './focused.js'
import type { ExecutionLedgerExpectation, SuiteConfig, TrackConfig, TrackEvaluation, TrackRun } from './types.js'

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

function assemblyPathFor(apphost: string): string {
  return apphost.endsWith('.exe') ? `${apphost.slice(0, -4)}.dll` : `${apphost}.dll`
}

function parallelismFor(track: TrackConfig): string {
  const args = track.apphostArgs ?? []
  const index = args.indexOf('-parallel')
  return index >= 0 && args[index + 1] ? `xunit-${args[index + 1]}` : 'xunit-default'
}

function executionLedgerExpectation(track: TrackConfig): ExecutionLedgerExpectation {
  if (!track.executionLedger || track.kind !== 'dotnet-apphost' || !track.csproj) {
    throw new Error(`track "${track.id}" requires a dotnet-apphost csproj for execution ledger evidence`)
  }
  const csprojAbs = resolve(repoRoot, track.csproj)
  const projectDir = dirname(csprojAbs)
  const xml = readCsproj(track.csproj)
  const assemblyName = parseAssemblyName(xml) ?? basename(csprojAbs).replace(/\.csproj$/, '')
  const apphost = resolve(projectDir, resolveApphostPath({ csprojXml: xml, projectDir, assemblyName }))
  const assemblyPath = assemblyPathFor(apphost)
  const discovery = resolveDiscoveryCommand({ csprojXml: xml, projectDir, assemblyName })
  let manifest
  try {
    manifest = discoverManifest({
      listTests: () => execFileSync(discovery.apphost, discovery.args as string[], { cwd: repoRoot, encoding: 'utf8' }),
    })
  } catch (error) {
    throw new Error(`compiled discovery failed: ${(error as Error).message}`)
  }
  return {
    runId: createExecutionRunId({ now: () => Date.now() }, randomUUID),
    manifest,
    assemblyPath,
    assemblySha256: sha256File(assemblyPath),
    parallelism: parallelismFor(track),
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

function signalTree(pid: number, signal: NodeJS.Signals): void {
  if (pid <= 1) return
  try {
    if (process.platform === 'win32') {
      execFileSync('taskkill', ['/pid', String(pid), '/T', '/F'], { stdio: 'ignore' })
    } else {
      process.kill(-pid, signal)
    }
  } catch {
    // already gone or not permitted
  }
}

interface SpawnedChild {
  readonly done: Promise<{ exitCode: number | null }>
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

function spawnChild(command: string, args: readonly string[], extraEnvironment?: Readonly<Record<string, string>>): SpawnedChild {
  const detached = process.platform !== 'win32'
  const child = spawn(command, args as string[], {
    cwd: repoRoot,
    stdio: 'inherit',
    detached,
    env: extraEnvironment ? { ...process.env, ...extraEnvironment } : undefined,
  })
  const done = new Promise<{ exitCode: number | null }>((resolvePromise) => {
    child.on('exit', (code) => resolvePromise({ exitCode: code }))
    child.on('error', () => resolvePromise({ exitCode: 1 }))
  })
  return { done, pid: child.pid ?? -1 }
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

async function killTree(child: SpawnedChild, graceMs: number): Promise<void> {
  if (process.platform === 'win32') {
    signalTree(child.pid, 'SIGKILL')
    return
  }

  signalTree(child.pid, 'SIGTERM')
  const termGrace = createTimeout(graceMs)
  const exited = await Promise.race([
    child.done.then(() => true),
    termGrace.promise.then(() => false),
  ])
  termGrace.cancel()
  signalTree(child.pid, 'SIGKILL')
  if (!exited) {
    const killGrace = createTimeout(graceMs)
    await Promise.race([
      child.done,
      killGrace.promise,
    ])
    killGrace.cancel()
  }
}

function runTrack(track: TrackConfig, graceMs: number, suiteDeadline: Promise<void>): Promise<TrackRun> {
  const reportPath = resolve(repoRoot, track.report)
  const ledgerPath = track.executionLedger ? resolve(repoRoot, track.executionLedger) : undefined
  let command: string
  let args: readonly string[]
  let ledgerExpectation: ExecutionLedgerExpectation | undefined
  let ledgerEnvironment: Readonly<Record<string, string>> | undefined
  try {
    if (ledgerPath === reportPath) throw new Error('execution ledger path must differ from the TRX report path')
    try {
      unlinkSync(reportPath)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
    }
    if (ledgerPath) {
      try {
        unlinkSync(ledgerPath)
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
      }
      ledgerExpectation = executionLedgerExpectation(track)
      ledgerEnvironment = buildLedgerEnvironment({
        ...ledgerExpectation,
        ledgerPath,
      })
    }
    ({ command, args } = commandFor(track))
  } catch (error) {
    const failed = failedRun(track, 'not started', `could not prepare report ${track.report}: ${(error as Error).message}`)
    return Promise.resolve(ledgerPath
      ? { ...failed, executionLedgerReady: false, executionLedgerError: (error as Error).message }
      : failed)
  }

  const cmdString = `${command} ${args.join(' ')}`
  let child: SpawnedChild
  try {
    child = spawnChild(command, args, ledgerEnvironment)
  } catch (error) {
    const failed = failedRun(track, cmdString, `could not start track: ${(error as Error).message}`)
    return Promise.resolve(ledgerPath
      ? { ...failed, executionLedgerReady: false, executionLedgerError: (error as Error).message }
      : failed)
  }
  const trackDeadline = createTimeout(track.deadlineMs)
  const outcome = runWithDeadline({
    start: () => child.done,
    kill: () => killTree(child, graceMs),
    timeout: Promise.race([
      trackDeadline.promise.then(() => 'track' as const),
      suiteDeadline.then(() => 'suite' as const),
    ]),
    now: () => Date.now(),
  })
  return outcome.then((result) => {
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
      elapsedMs: result.elapsedMs,
      deadlineMs: track.deadlineMs,
      command: cmdString,
      reportReady: ready,
      reportError: ready ? undefined : `report ${track.report} was not created or refreshed by the track`,
      executionLedgerReady,
      executionLedgerError,
      executionLedgerExpectation: ledgerExpectation,
    }
  }).finally(trackDeadline.cancel)
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

function evaluateFromFile(track: TrackConfig, run?: TrackRun): TrackEvaluation {
  if (run && !run.reportReady) {
    return failedEvaluation(track, run.reportError ?? `report ${track.report} was not refreshed`)
  }
  if (track.executionLedger && run && !run.executionLedgerReady) {
    return failedEvaluation(track, run.executionLedgerError ?? `execution ledger ${track.executionLedger} was not refreshed`)
  }
  try {
    const trxContent = readFileSync(resolve(repoRoot, track.report), 'utf8')
    const trxCases = parseReport(track.reportFormat, trxContent)
    if (track.executionLedger) {
      if (!run?.executionLedgerExpectation) return failedEvaluation(track, 'execution ledger expectation was not captured before the run')
      const ledgerContent = readFileSync(resolve(repoRoot, track.executionLedger), 'utf8')
      const parsedLedger = parseExecutionLedger(ledgerContent)
      const evidence = validateExecutionEvidence(trxCases, parsedLedger, run.executionLedgerExpectation)
      if (evidence.errors.length > 0) {
        return failedEvaluation(track, `execution ledger contract failed: ${evidence.errors.join('; ')}`)
      }
      return evaluateTrack(track, evidence.cases, new Date())
    }
    return evaluateTrack(track, trxCases, new Date())
  } catch (error) {
    return failedEvaluation(track, `could not read report ${track.report}: ${(error as Error).message}`)
  }
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
    const runFailed = suiteDeadlineBreached || runs.some((r) => r.timedOut || r.exitCode !== 0)
    const budgetFailed = evaluations.some((e) => !e.passed)
    return runFailed || budgetFailed ? 1 : 0
  }

  for (const track of selected) {
    evaluations.push(evaluateFromFile(track))
  }
  console.log('budget:')
  for (const evaluation of evaluations) {
    for (const line of formatEvaluation(evaluation)) console.log(line)
  }
  const summary = summarize(runs, evaluations)
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
