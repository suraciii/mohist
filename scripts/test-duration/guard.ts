import { execFileSync, spawn } from 'node:child_process'
import { createWriteStream, existsSync, mkdirSync, readFileSync, statSync, unlinkSync, writeFileSync } from 'node:fs'
import { dirname, resolve, basename, isAbsolute, join } from 'node:path'
import { finished } from 'node:stream/promises'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { evaluateTrack } from './budget.js'
import {
  buildStampMatchesRun,
  createArtifactRoot as createUniqueArtifactRoot,
  isInsideDirectory,
  parseCanonicalRunMetadata,
  type CanonicalRunMetadata,
} from './artifacts.js'
import { parseSuiteConfig, validateConfig } from './config.js'
import { formatEvaluation, formatSummary, formatTrackRun, summarize } from './diagnostics.js'
import {
  externalAbortCleanupDeadlineAt,
  runWithDeadline,
  suiteDeadlines,
  suiteDeadlinesAt,
  type SuiteDeadlines,
} from './deadline.js'
import { parseReport } from './reports.js'
import { parseAssemblyName, resolveApphostPath, resolveFocusedCommand } from './focused.js'
import { nativeProcessTreeOps, terminateProcessTree, type ProcessTreeOps } from './process-tree.js'
import { scheduleLanes, type LaneSpec, type RunningLane } from './scheduler.js'
import { nativeCalendarSource, nativeTimeSource } from './time.js'
import type { SuiteConfig, TestCase, TrackConfig, TrackEvaluation, TrackRun } from './types.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

function readCsproj(csprojPath: string): string {
  return readFileSync(resolve(repoRoot, csprojPath), 'utf8')
}

function apphostFor(track: TrackConfig): string {
  if (track.apphost) return resolveApphostExecutable(resolve(repoRoot, track.apphost))
  if (!track.csproj) throw new Error(`track "${track.id}": dotnet-apphost needs csproj or apphost`)
  const csprojAbs = resolve(repoRoot, track.csproj)
  const csprojDir = dirname(csprojAbs)
  const xml = readCsproj(track.csproj)
  // MSBuild defaults AssemblyName to the project file name when omitted.
  const assemblyName =
    track.apphost ? undefined : parseAssemblyName(xml) ?? basename(csprojAbs).replace(/\.csproj$/, '')
  return resolveApphostExecutable(resolve(csprojDir, resolveApphostPath({ csprojXml: xml, projectDir: csprojDir, assemblyName })))
}

function resolveApphostExecutable(path: string): string {
  return process.platform === 'win32' && !path.toLowerCase().endsWith('.exe') ? `${path}.exe` : path
}

export function commandFor(track: TrackConfig, reportRoot: string = repoRoot): { command: string; args: readonly string[] } {
  if (track.kind === 'dotnet-apphost') {
    const apphost = apphostFor(track)
    return {
      command: apphost,
      args: ['-noColor', '-noLogo', '-noAutoReporters', '-trx', resolve(reportRoot, track.report), ...(track.apphostArgs ?? [])],
    }
  }
  if (track.kind === 'dotnet-vstest') {
    if (!track.csproj) throw new Error(`track "${track.id}": dotnet-vstest needs csproj`)
    const reportDir = resolve(reportRoot, dirname(track.report))
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
    const reportAbs = resolve(reportRoot, track.report)
    const args = track.run.slice(1).map((arg) => arg.replace('{report}', reportAbs))
    return { command: track.run[0], args }
  }
  throw new Error(`track "${track.id}": no run command`)
}

interface SpawnedChild {
  readonly done: Promise<{ exitCode: number | null }>
  readonly pid: number
}

interface RawEvidence {
  readonly stdoutPath: string
  readonly stderrPath: string
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

export interface GuardRuntime {
  readonly now: () => number
  readonly calendarNow?: () => Date
  readonly timeoutScheduler?: TimeoutScheduler
  readonly processTreeOps?: ProcessTreeOps
  readonly abortSignal?: AbortSignal
}

const nativeGuardRuntime: GuardRuntime = {
  now: nativeTimeSource.now,
  calendarNow: nativeCalendarSource.now,
}

export function calendarNowFor(runtime: Pick<GuardRuntime, 'calendarNow'>): () => Date {
  return runtime.calendarNow ?? nativeCalendarSource.now
}

export function evaluateTrackAtCalendarDate(
  track: TrackConfig,
  cases: readonly TestCase[],
  runtime: Pick<GuardRuntime, 'calendarNow'>,
): TrackEvaluation {
  return evaluateTrack(track, cases, calendarNowFor(runtime)())
}

export function reportEvaluationFailureReason(
  now: number,
  deadlines: SuiteDeadlines,
  externallyAborted: boolean,
): string | undefined {
  if (externallyAborted) {
    return 'external termination stopped report evaluation before the canonical cleanup wall'
  }
  if (now >= deadlines.executionDeadlineAt) {
    return 'suite execution cutoff reached before report evaluation'
  }
  return undefined
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
  env: NodeJS.ProcessEnv,
  evidence: RawEvidence,
): SpawnedChild {
  const detached = process.platform !== 'win32'
  mkdirSync(dirname(evidence.stdoutPath), { recursive: true })
  const stdout = createWriteStream(evidence.stdoutPath)
  const stderr = createWriteStream(evidence.stderrPath)
  const child = spawn(command, args as string[], {
    cwd: repoRoot,
    env,
    stdio: ['ignore', 'pipe', 'pipe'],
    detached,
  })
  child.stdout?.on('data', (chunk: Buffer) => {
    stdout.write(chunk)
    process.stdout.write(chunk)
  })
  child.stderr?.on('data', (chunk: Buffer) => {
    stderr.write(chunk)
    process.stderr.write(chunk)
  })
  const done = new Promise<{ exitCode: number | null }>((resolvePromise) => {
    let settled = false
    const settle = async (code: number | null) => {
      if (settled) return
      settled = true
      stdout.end()
      stderr.end()
      await Promise.allSettled([finished(stdout), finished(stderr)])
      resolvePromise({ exitCode: code })
    }
    child.once('error', () => { void settle(1) })
    child.once('close', (code) => { void settle(code) })
  })
  return { done, pid: child.pid ?? -1 }
}

export function reportFileReady(reportPath: string): boolean {
  return existsSync(reportPath) && statSync(reportPath).isFile() && statSync(reportPath).size > 0
}

export interface ReportPreparationOps {
  readonly mkdir: (directory: string) => void
  readonly unlink: (path: string) => void
}

const nativeReportPreparationOps: ReportPreparationOps = {
  mkdir: (directory) => mkdirSync(directory, { recursive: true }),
  unlink: unlinkSync,
}

export function prepareReportTarget(reportPath: string, ops: ReportPreparationOps = nativeReportPreparationOps): void {
  ops.mkdir(dirname(reportPath))
  try {
    ops.unlink(reportPath)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
  }
}

export interface LaneSandbox {
  readonly tempDir: string
  readonly ipcDir: string
  readonly homeDir: string
  readonly databasePath: string
  readonly otelDatabasePath: string
  readonly otelPort: number
  readonly environment: NodeJS.ProcessEnv
}

const laneOtelPortStart = 43_000

export function laneSandbox(
  artifactRoot: string,
  laneId: string,
  inherited: NodeJS.ProcessEnv = process.env,
  sandboxOrdinal = 0,
  isolateServerRuntime = false,
): LaneSandbox {
  const laneRoot = resolve(artifactRoot, 'tmp', laneId)
  const tempDir = resolve(laneRoot, 'temp')
  const ipcDir = resolve(laneRoot, 'ipc')
  const homeDir = resolve(laneRoot, 'home')
  const databasePath = resolve(laneRoot, 'mohist', 'mohist.db')
  const otelDatabasePath = resolve(laneRoot, 'mohist', 'otel.db')
  const otelPort = laneOtelPortStart + sandboxOrdinal
  return {
    tempDir,
    ipcDir,
    homeDir,
    databasePath,
    otelDatabasePath,
    otelPort,
    environment: {
      ...inherited,
      TMPDIR: tempDir,
      TEMP: tempDir,
      TMP: tempDir,
      XDG_RUNTIME_DIR: ipcDir,
      HOME: homeDir,
      USERPROFILE: homeDir,
      MOHIST_TEST_LANE: laneId,
      ...(isolateServerRuntime
        ? {
            // Only concurrent Spec lanes need isolated server-owned paths and
            // ports. Unit lanes retain their product-default assertions.
            MOHIST_DB_PATH: databasePath,
            MOHIST_OTEL_DB_PATH: otelDatabasePath,
            MOHIST__Otel__DbPath: otelDatabasePath,
            MOHIST__Otel__BindHost: '127.0.0.1',
            MOHIST__Otel__Port: String(otelPort),
            MOHIST__Otel__Endpoint: `http://127.0.0.1:${otelPort}/otel`,
            OTEL_EXPORTER_OTLP_ENDPOINT: `http://127.0.0.1:${otelPort}`,
          }
        : {}),
    },
  }
}

export function isLaneSuccessful(run: TrackRun): boolean {
  return !run.cancelled && !run.timedOut && run.exitCode === 0 && run.reportReady && run.cleanupComplete
}

interface PlannedLane {
  readonly lane: LaneSpec
  readonly policyTrack?: TrackConfig
  readonly executionTrack?: TrackConfig
  readonly reportPath?: string
  readonly partition?: number
  readonly sandboxOrdinal: number
  readonly deadlineMs: number
}

function laneResources(track: TrackConfig): string[] {
  return ['host', track.kind === 'vitest' ? 'node' : 'dotnet']
}

function withLaneConstraints(
  plan: PlannedLane,
  dependsOn: readonly string[] = [],
  resources: readonly string[] = [],
): PlannedLane {
  const laneDependencies = [...new Set([...(plan.lane.dependsOn ?? []), ...dependsOn])]
  const laneResources = [...new Set([...(plan.lane.resources ?? []), ...resources])]
  return {
    ...plan,
    lane: {
      ...plan.lane,
      ...(laneDependencies.length > 0 ? { dependsOn: laneDependencies } : {}),
      ...(laneResources.length > 0 ? { resources: laneResources } : {}),
    },
  }
}

function applyDurationMeasurementPhase(
  planned: readonly PlannedLane[],
  durationMeasurementTracks: readonly string[],
  durationIsolationTrack?: string,
): PlannedLane[] {
  if (durationMeasurementTracks.length === 0) return [...planned]
  if (new Set(durationMeasurementTracks).size !== durationMeasurementTracks.length) return [...planned]

  const measurementLaneIds: string[] = []
  for (const trackId of durationMeasurementTracks) {
    const matching = planned.filter((plan) => plan.policyTrack?.id === trackId)
    if (matching.length !== 1) return [...planned]
    measurementLaneIds.push(matching[0].lane.id)
  }

  const finalMeasurementLaneId = measurementLaneIds[measurementLaneIds.length - 1]
  const isolationLaneId = durationIsolationTrack === undefined
    ? undefined
    : planned.find((plan) => plan.policyTrack?.id === durationIsolationTrack)?.lane.id
  return planned.map((plan) => {
    const measurementIndex = plan.policyTrack === undefined
      ? -1
      : durationMeasurementTracks.indexOf(plan.policyTrack.id)
    if (measurementIndex >= 0) {
      const predecessor = measurementIndex === 0 ? [] : [measurementLaneIds[measurementIndex - 1]]
      return withLaneConstraints(plan, predecessor, ['duration-measurement'])
    }
    const dependencies = isolationLaneId !== undefined && plan.lane.id !== isolationLaneId && plan.policyTrack?.kind === 'vitest'
      ? [isolationLaneId]
      : [finalMeasurementLaneId]
    const resources = plan.lane.id === isolationLaneId ? ['duration-measurement'] : []
    return withLaneConstraints(plan, dependencies, resources)
  })
}

export function planTracks(
  selected: readonly TrackConfig[],
  artifactRoot: string,
  durationMeasurementTracks: readonly string[] = [],
  durationIsolationTrack?: string,
): PlannedLane[] {
  const planned: PlannedLane[] = []
  for (const track of selected) {
    if (track.partitions === undefined) {
      planned.push({
        lane: { id: track.id, resources: laneResources(track) },
        policyTrack: track,
        executionTrack: track,
        reportPath: resolve(artifactRoot, track.report),
        sandboxOrdinal: planned.length,
        deadlineMs: track.deadlineMs,
      })
      continue
    }

    const partitionLaneIds: string[] = []
    for (let partition = 0; partition < track.partitions; partition++) {
      const id = `${track.id}-${partition}`
      partitionLaneIds.push(id)
      const report = track.report.replace('{partition}', String(partition))
      planned.push({
        lane: {
          id,
          resources: [...laneResources(track), 'server-spec', `spec-report-${partition}`, `spec-temp-${partition}`, `spec-port-${partition}`],
          resourceWeights: { 'server-spec': track.partitionMaxThreads ?? 1 },
        },
        policyTrack: track,
        executionTrack: { ...track, id, report },
        reportPath: resolve(artifactRoot, report),
        partition,
        sandboxOrdinal: planned.length,
        deadlineMs: track.deadlineMs,
      })
    }
    planned.push({
      lane: { id: `${track.id}-coverage`, dependsOn: partitionLaneIds, resources: ['host'] },
      sandboxOrdinal: planned.length,
      deadlineMs: track.deadlineMs,
    })
  }
  return applyDurationMeasurementPhase(planned, durationMeasurementTracks, durationIsolationTrack)
}

function evidenceFor(artifactRoot: string, laneId: string): RawEvidence {
  return {
    stdoutPath: resolve(artifactRoot, 'logs', `${laneId}.stdout.log`),
    stderrPath: resolve(artifactRoot, 'logs', `${laneId}.stderr.log`),
  }
}

function failedRun(plan: PlannedLane, command: string, reportError: string, evidence?: RawEvidence): TrackRun {
  return {
    trackId: plan.lane.id,
    policyTrackId: plan.policyTrack?.id,
    reportPath: plan.reportPath,
    timedOut: false,
    exitCode: 1,
    elapsedMs: 0,
    deadlineMs: plan.deadlineMs,
    command,
    reportReady: false,
    cleanupComplete: true,
    reportError,
    stdoutPath: evidence?.stdoutPath,
    stderrPath: evidence?.stderrPath,
  }
}

function cancelledRun(
  plan: PlannedLane,
  suiteExpired: boolean,
  artifactRoot: string,
  failureLaneId?: string,
): TrackRun {
  const cancellationReason = suiteExpired
    ? 'after the suite deadline expired'
    : failureLaneId
      ? `after ${failureLaneId} failed`
      : 'after the scheduler aborted'
  return {
    trackId: plan.lane.id,
    policyTrackId: plan.policyTrack?.id,
    reportPath: plan.reportPath,
    cancelled: true,
    cancellationReason,
    timedOut: suiteExpired,
    timeoutReason: suiteExpired ? 'suite' : undefined,
    exitCode: null,
    elapsedMs: 0,
    deadlineMs: plan.deadlineMs,
    command: `not started: cancelled ${cancellationReason}`,
    reportReady: false,
    cleanupComplete: true,
    reportError: plan.reportPath
      ? `report ${plan.reportPath} was not produced ${cancellationReason}`
      : `coverage verification did not run ${cancellationReason}`,
    ...evidenceFor(artifactRoot, plan.lane.id),
  }
}

export function cleanupDeadlineAt(now: number, hardDeadlineAt: number, graceMs: number): number {
  return Math.min(hardDeadlineAt, now + graceMs * 2)
}

async function killTree(
  child: SpawnedChild,
  graceMs: number,
  hardDeadlineAt: number,
  processTreeOps: ProcessTreeOps,
): Promise<boolean> {
  return terminateProcessTree(child, hardDeadlineAt, graceMs, processTreeOps)
}

export function specPartitionCommand(args: readonly string[]): { readonly command: string; readonly args: readonly string[] } {
  return {
    command: process.execPath,
    args: ['--import', 'tsx', resolve(repoRoot, 'scripts/test-duration/spec-partition.ts'), ...args],
  }
}

function startLane(
  plan: PlannedLane,
  graceMs: number,
  suiteDeadline: Promise<void>,
  deadlines: SuiteDeadlines,
  artifactRoot: string,
  runtime: GuardRuntime,
  cancellationDeadlineAt: () => number,
): RunningLane<TrackRun> {
  const evidence = evidenceFor(artifactRoot, plan.lane.id)
  let command: string
  let args: readonly string[]
  try {
    if (plan.reportPath) {
      prepareReportTarget(plan.reportPath)
    }
    if (plan.executionTrack && plan.partition !== undefined) {
      const apphost = apphostFor(plan.executionTrack)
      const manifestDir = join(artifactRoot, 'manifests', 'server-spec', `partition-${plan.partition}`)
      ;({ command, args } = specPartitionCommand([
        'run',
        apphost,
        String(plan.partition),
        String(plan.executionTrack.partitions),
        manifestDir,
        plan.reportPath!,
        ...(plan.executionTrack.partitionMaxThreads === undefined
          ? []
          : [String(plan.executionTrack.partitionMaxThreads)]),
      ]))
    } else if (plan.executionTrack) {
      ({ command, args } = commandFor(plan.executionTrack, artifactRoot))
    } else {
      ;({ command, args } = specPartitionCommand(['verify', resolve(artifactRoot, 'manifests', 'server-spec')]))
    }
  } catch (error) {
    return {
      result: Promise.resolve(failedRun(plan, 'not started', `could not prepare lane: ${(error as Error).message}`, evidence)),
      cancel: () => {},
    }
  }

  const cmdString = `${command} ${args.join(' ')}`
  let child: SpawnedChild
  try {
    const sandbox = laneSandbox(
      artifactRoot,
      plan.lane.id,
      process.env,
      plan.sandboxOrdinal,
      plan.lane.resources?.includes('server-spec'),
    )
    mkdirSync(sandbox.tempDir, { recursive: true })
    mkdirSync(sandbox.ipcDir, { recursive: true })
    mkdirSync(sandbox.homeDir, { recursive: true })
    mkdirSync(dirname(sandbox.databasePath), { recursive: true })
    mkdirSync(dirname(sandbox.otelDatabasePath), { recursive: true })
    child = spawnChild(command, args, sandbox.environment, evidence)
  } catch (error) {
    return {
      result: Promise.resolve(failedRun(plan, cmdString, `could not start lane: ${(error as Error).message}`, evidence)),
      cancel: () => {},
    }
  }
  const trackDeadline = createTimeout(plan.deadlineMs, runtime.timeoutScheduler)
  let cleanupComplete = true
  let cancellation: Promise<void> | undefined
  const cancel = () => {
    cancellation ??= killTree(
      child,
      graceMs,
      cancellationDeadlineAt(),
      { ...(runtime.processTreeOps ?? nativeProcessTreeOps), now: runtime.now },
    ).then((completed) => { cleanupComplete = completed })
    return cancellation
  }
  const outcome = runWithDeadline({
    start: () => child.done,
    kill: cancel,
    timeout: Promise.race([
      trackDeadline.promise.then(() => 'track' as const),
      suiteDeadline.then(() => 'suite' as const),
    ]),
    now: runtime.now,
    hardDeadlineAt: deadlines.hardDeadlineAt,
  })
  const result = outcome.then(async (outcomeResult) => {
    if (outcomeResult.status !== 'timeout') {
      cleanupComplete = await killTree(
        child,
        graceMs,
        cancellationDeadlineAt(),
        { ...(runtime.processTreeOps ?? nativeProcessTreeOps), now: runtime.now },
      )
    }
    let ready = false
    try {
      ready = plan.reportPath ? reportFileReady(plan.reportPath) : outcomeResult.status === 'passed'
    } catch {
      ready = false
    }
    return {
      trackId: plan.lane.id,
      policyTrackId: plan.policyTrack?.id,
      reportPath: plan.reportPath,
      timedOut: outcomeResult.status === 'timeout',
      timeoutReason: outcomeResult.timeoutReason,
      exitCode: outcomeResult.exitCode,
      elapsedMs: outcomeResult.elapsedMs,
      deadlineMs: plan.deadlineMs,
      command: cmdString,
      reportReady: ready,
      cleanupComplete,
      reportError: ready
        ? undefined
        : !cleanupComplete
          ? `lane process tree did not reach a terminal state before ${plan.lane.id} cancellation completed`
        : plan.reportPath
          ? `report ${plan.reportPath} was not created or refreshed by the lane`
          : 'coverage verification did not complete',
      stdoutPath: evidence.stdoutPath,
      stderrPath: evidence.stderrPath,
    }
  }).finally(trackDeadline.cancel)
  return { result, cancel }
}

function failedEvaluation(track: TrackConfig, reportError: string): TrackEvaluation {
  return {
    trackId: track.id,
    enforce: track.enforce,
    status: track.status,
    reason: track.reason,
    reportError,
    total: 0,
    outcomes: { total: 0, passed: 0, failed: 0, errors: 0, skipped: 0, notRun: 0, other: 0 },
    failedTests: [],
    rules: [],
    passed: false,
  }
}

function evaluateFromPlans(
  track: TrackConfig,
  plans: readonly PlannedLane[],
  runsByLane: ReadonlyMap<string, TrackRun>,
  runtime: Pick<GuardRuntime, 'calendarNow'>,
): TrackEvaluation {
  const cases = []
  for (const plan of plans) {
    if (!plan.reportPath) continue
    const run = runsByLane.get(plan.lane.id)
    if (run?.cancelled) {
      const state = run.reportReady ? 'report is ignored after cancellation' : run.reportError ?? 'report was not produced'
      return failedEvaluation(track, `lane ${plan.lane.id} was cancelled ${run.cancellationReason ?? ''}; ${state}`)
    }
    if (run && !run.reportReady) {
      return failedEvaluation(track, run.reportError ?? `report ${plan.reportPath} was not refreshed`)
    }
    try {
      const content = readFileSync(plan.reportPath, 'utf8')
      cases.push(...parseReport(track.reportFormat, content))
    } catch (error) {
      return failedEvaluation(track, `could not read report ${plan.reportPath}: ${(error as Error).message}`)
    }
  }
  return evaluateTrackAtCalendarDate(track, cases, runtime)
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
  artifactRoot?: string
  runRoot?: string
  suiteDeadlineMs?: number
  suiteDeadlineAtMs?: number
  requireBuildStamp: boolean
  focused?: { csproj: string; className: string }
}

export function parseArgs(argv: readonly string[]): Args {
  const tracks: string[] = []
  let mode: 'run' | 'check' | 'focused' = 'run'
  let all = false
  let artifactRoot: string | undefined
  let runRoot: string | undefined
  let suiteDeadlineMs: number | undefined
  let suiteDeadlineAtMs: number | undefined
  let requireBuildStamp = false
  let focused: { csproj: string; className: string } | undefined
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--check') mode = 'check'
    else if (arg === '--all') all = true
    else if (arg === '--track') tracks.push(argv[++i])
    else if (arg.startsWith('--track=')) tracks.push(arg.slice('--track='.length))
    else if (arg === '--artifact-root') artifactRoot = argv[++i]
    else if (arg.startsWith('--artifact-root=')) artifactRoot = arg.slice('--artifact-root='.length)
    else if (arg === '--run-root') runRoot = argv[++i]
    else if (arg.startsWith('--run-root=')) runRoot = arg.slice('--run-root='.length)
    else if (arg === '--suite-deadline-ms') suiteDeadlineMs = Number(argv[++i])
    else if (arg.startsWith('--suite-deadline-ms=')) suiteDeadlineMs = Number(arg.slice('--suite-deadline-ms='.length))
    else if (arg === '--suite-deadline-at-ms') suiteDeadlineAtMs = Number(argv[++i])
    else if (arg.startsWith('--suite-deadline-at-ms=')) suiteDeadlineAtMs = Number(arg.slice('--suite-deadline-at-ms='.length))
    else if (arg === '--require-build-stamp') requireBuildStamp = true
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
  return { mode, tracks, all, artifactRoot, runRoot, suiteDeadlineMs, suiteDeadlineAtMs, requireBuildStamp, focused }
}

function isMatchingCanonicalBuild(root: string): boolean {
  try {
    return buildStampMatchesRun(
      readFileSync(resolve(root, 'run.json'), 'utf8'),
      readFileSync(resolve(root, 'build-stamp.json'), 'utf8'),
    )
  } catch {
    return false
  }
}

function hasCanonicalRunMetadata(root: string): boolean {
  try {
    return parseCanonicalRunMetadata(readFileSync(resolve(root, 'run.json'), 'utf8')) !== undefined
  } catch {
    return false
  }
}

function readCanonicalRunMetadata(root: string): CanonicalRunMetadata | undefined {
  try {
    return parseCanonicalRunMetadata(readFileSync(resolve(root, 'run.json'), 'utf8'))
  } catch {
    return undefined
  }
}

function externalAbsolutePath(value: string, argumentName: string): string {
  if (!isAbsolute(value)) throw new Error(`${argumentName} must be an absolute path outside the repository`)
  const path = resolve(value)
  if (isInsideDirectory(path, repoRoot)) {
    throw new Error(`${argumentName} must be outside the repository: ${path}`)
  }
  return path
}

function writeJsonEvidence(root: string, name: string, value: unknown): boolean {
  try {
    writeFileSync(resolve(root, name), `${JSON.stringify(value, null, 2)}\n`)
    return true
  } catch (error) {
    process.stderr.write(`could not write ${name}: ${(error as Error).message}\n`)
    return false
  }
}

export async function main(
  argv: readonly string[] = process.argv.slice(2),
  runtime: GuardRuntime = nativeGuardRuntime,
): Promise<number> {
  const {
    mode,
    tracks,
    all,
    artifactRoot: artifactRootArg,
    runRoot: runRootArg,
    suiteDeadlineMs: requestedDeadlineMs,
    suiteDeadlineAtMs: requestedDeadlineAtMs,
    requireBuildStamp,
    focused,
  } = parseArgs(argv)

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

  if (requestedDeadlineMs !== undefined && (!Number.isFinite(requestedDeadlineMs) || requestedDeadlineMs <= 0)) {
    process.stderr.write('--suite-deadline-ms must be a positive number\n')
    return 2
  }
  if (requestedDeadlineAtMs !== undefined && (!Number.isFinite(requestedDeadlineAtMs) || requestedDeadlineAtMs <= 0)) {
    process.stderr.write('--suite-deadline-at-ms must be a positive millisecond deadline\n')
    return 2
  }
  if (requestedDeadlineMs !== undefined && requestedDeadlineAtMs !== undefined) {
    process.stderr.write('--suite-deadline-ms and --suite-deadline-at-ms are mutually exclusive\n')
    return 2
  }
  if (artifactRootArg === '') {
    process.stderr.write('--artifact-root must not be empty\n')
    return 2
  }
  if (runRootArg === '') {
    process.stderr.write('--run-root must not be empty\n')
    return 2
  }
  if (mode === 'check' && runRootArg !== undefined) {
    process.stderr.write('--run-root is only valid for a canonical scheduler run\n')
    return 2
  }
  if (mode === 'check' && artifactRootArg === undefined) {
    process.stderr.write('--check requires an existing --artifact-root\n')
    return 2
  }
  if (mode === 'run' && runRootArg !== undefined && artifactRootArg !== undefined) {
    process.stderr.write('--run-root and --artifact-root are mutually exclusive\n')
    return 2
  }

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

  const graceMs = config.killGraceMs ?? 5000
  let suppliedRunRoot: string | undefined
  if (runRootArg !== undefined) {
    try {
      suppliedRunRoot = externalAbsolutePath(runRootArg, '--run-root')
    } catch (error) {
      process.stderr.write(`${(error as Error).message}\n`)
      return 2
    }
  }
  const canonicalRun = mode === 'run' && suppliedRunRoot !== undefined
    ? readCanonicalRunMetadata(suppliedRunRoot)
    : undefined
  if (runRootArg !== undefined && canonicalRun === undefined) {
    process.stderr.write(`canonical run metadata is missing or invalid: ${resolve(suppliedRunRoot!, 'run.json')}\n`)
    return 1
  }
  if (canonicalRun !== undefined && canonicalRun.suiteDeadlineMs !== config.suiteDeadlineMs) {
    process.stderr.write(
      `canonical run deadline ${canonicalRun.suiteDeadlineMs}ms does not match configured ${config.suiteDeadlineMs}ms\n`,
    )
    return 1
  }
  const suiteStart = canonicalRun?.startedAt ?? runtime.now()
  if (runtime.now() < suiteStart) {
    process.stderr.write('canonical run start is in the future\n')
    return 1
  }
  const configuredDeadlineAt = suiteStart + config.suiteDeadlineMs
  const deadlines = requestedDeadlineAtMs === undefined
    ? suiteDeadlines(suiteStart, Math.min(config.suiteDeadlineMs, requestedDeadlineMs ?? config.suiteDeadlineMs), graceMs)
    : suiteDeadlinesAt(Math.min(configuredDeadlineAt, requestedDeadlineAtMs), graceMs)
  const suiteDeadlineMs = Math.max(0, deadlines.hardDeadlineAt - suiteStart)

  let artifactRoot: string
  if (mode === 'check') {
    artifactRoot = resolve(repoRoot, artifactRootArg!)
  } else if (suppliedRunRoot !== undefined) {
    artifactRoot = suppliedRunRoot
    if (!hasCanonicalRunMetadata(artifactRoot)) {
      process.stderr.write(`canonical run metadata is missing: ${resolve(artifactRoot, 'run.json')}\n`)
      return 1
    }
  } else {
    try {
      const artifactParent = artifactRootArg === undefined
        ? undefined
        : externalAbsolutePath(artifactRootArg, '--artifact-root')
      const runId = `${suiteStart}-${process.pid}`
      artifactRoot = createUniqueArtifactRoot(runId, repoRoot, artifactParent)
      writeFileSync(
        resolve(artifactRoot, 'run.json'),
        JSON.stringify({ runId, startedAt: suiteStart, suiteDeadlineMs }, null, 2) + '\n',
      )
      console.log(`test-duration diagnostics: ${artifactRoot}`)
    } catch (error) {
      process.stderr.write(`${(error as Error).message}\n`)
      return 2
    }
  }
  if (requireBuildStamp && !isMatchingCanonicalBuild(artifactRoot)) {
    process.stderr.write(`fresh matching build stamp is missing: ${resolve(artifactRoot, 'build-stamp.json')}\n`)
    return 1
  }

  const planned = planTracks(
    selected,
    artifactRoot,
    config.canonical?.durationMeasurementTracks,
    config.canonical?.durationIsolationTrack,
  )
  const plansByPolicy = new Map<string, PlannedLane[]>()
  for (const plan of planned) {
    if (!plan.policyTrack) continue
    const existing = plansByPolicy.get(plan.policyTrack.id) ?? []
    existing.push(plan)
    plansByPolicy.set(plan.policyTrack.id, existing)
  }

  if (mode === 'run' && !writeJsonEvidence(artifactRoot, 'plan.json', {
    sourceRevision: canonicalRun?.sourceRevision,
    suiteStart,
    hardDeadlineAt: deadlines.hardDeadlineAt,
    executionDeadlineAt: deadlines.executionDeadlineAt,
    selectedTracks: selected.map((track) => track.id),
    lanes: planned.map((plan) => ({
      id: plan.lane.id,
      policyTrackId: plan.policyTrack?.id,
      dependsOn: plan.lane.dependsOn ?? [],
      resources: plan.lane.resources ?? [],
      resourceWeights: plan.lane.resourceWeights ?? {},
      reportPath: plan.reportPath,
      partition: plan.partition,
      sandboxOrdinal: plan.sandboxOrdinal,
      deadlineMs: plan.deadlineMs,
    })),
  })) return 1

  const runs: TrackRun[] = []
  const evaluations: TrackEvaluation[] = []

  if (mode === 'run') {
    let suiteExpired = false
    let suiteAbortReason: 'deadline' | 'external' | undefined
    let externalCleanupDeadline: number | undefined
    let schedulerFailureLaneId: string | undefined
    const suiteAbort = new AbortController()
    let resolveSuiteDeadline!: () => void
    const suiteDeadline = new Promise<void>((resolvePromise) => { resolveSuiteDeadline = resolvePromise })
    const expireSuite = (reason: 'deadline' | 'external') => {
      if (suiteExpired) return
      suiteExpired = true
      suiteAbortReason = reason
      if (reason === 'external') {
        externalCleanupDeadline = externalAbortCleanupDeadlineAt(
          runtime.now(),
          deadlines.hardDeadlineAt,
          graceMs,
        )
      }
      suiteAbort.abort()
      resolveSuiteDeadline()
    }
    const externalAbort = runtime.abortSignal
    const abortFromCanonical = () => expireSuite('external')
    if (externalAbort?.aborted) abortFromCanonical()
    else externalAbort?.addEventListener('abort', abortFromCanonical, { once: true })
    const executionNow = runtime.now()
    const executionTimer = executionNow >= deadlines.executionDeadlineAt
      ? undefined
      : createTimeout(Math.max(0, deadlines.executionDeadlineAt - executionNow), runtime.timeoutScheduler)
    if (executionTimer === undefined) expireSuite('deadline')
    else void executionTimer.promise.then(() => expireSuite('deadline'))
    try {
      const lanesById = new Map(planned.map((plan) => [plan.lane.id, plan]))
      const resourceLimits = {
        ...(config.canonical?.resourceLimits ?? {}),
        host: Math.min(
          config.canonical?.maxConcurrentLanes ?? 1,
          config.canonical?.resourceLimits.host ?? config.canonical?.maxConcurrentLanes ?? 1,
        ),
      }
      const scheduled = await scheduleLanes(
        planned.map((plan) => plan.lane),
        (lane) => startLane(
          lanesById.get(lane.id)!,
          graceMs,
          suiteDeadline,
          deadlines,
          artifactRoot,
          runtime,
          () => externalCleanupDeadline
            ?? cleanupDeadlineAt(runtime.now(), deadlines.hardDeadlineAt, graceMs),
        ),
        isLaneSuccessful,
        { resourceLimits, abort: suiteAbort.signal },
      )
      schedulerFailureLaneId = scheduled.failureLaneId
      for (const scheduledLane of scheduled.lanes) {
        const plan = lanesById.get(scheduledLane.lane.id)!
        const result = scheduledLane.result
          ?? (scheduledLane.state === 'failed'
            ? failedRun(plan, 'scheduler', 'lane execution rejected before producing a report', evidenceFor(artifactRoot, plan.lane.id))
            : cancelledRun(plan, suiteExpired, artifactRoot, scheduled.failureLaneId))
        const run = scheduledLane.state === 'cancelled' && !result.cancelled
          ? {
              ...result,
              cancelled: true,
              cancellationReason: suiteExpired
                ? suiteAbortReason === 'external'
                  ? 'after the canonical process received an external termination signal'
                  : 'after the suite deadline expired'
                : scheduled.failureLaneId
                  ? `after ${scheduled.failureLaneId} failed`
                  : 'after the scheduler aborted',
            }
          : result
        runs.push(run)
        if (run.timeoutReason === 'track') console.error(`  ${run.trackId}: exceeded ${run.deadlineMs}ms deadline`)
      }
    } finally {
      executionTimer?.cancel()
    }
    const suiteElapsed = runtime.now() - suiteStart
    let suiteDeadlineBreached = suiteExpired || runtime.now() >= deadlines.hardDeadlineAt
    if (suiteDeadlineBreached) {
      console.error(`suite deadline breached after ${suiteElapsed}ms`)
    }

    const runsByTrack = new Map(runs.map((run) => [run.trackId, run]))
    for (const track of selected) {
      const beforeEvaluationFailure = reportEvaluationFailureReason(
        runtime.now(),
        deadlines,
        suiteAbortReason === 'external',
      )
      if (beforeEvaluationFailure !== undefined) {
        evaluations.push(failedEvaluation(track, beforeEvaluationFailure))
        continue
      }
      try {
        const evaluation = evaluateFromPlans(
          track,
          plansByPolicy.get(track.id) ?? [],
          runsByTrack,
          runtime,
        )
        const afterEvaluationFailure = reportEvaluationFailureReason(
          runtime.now(), deadlines, suiteAbortReason === 'external')
        if (afterEvaluationFailure !== undefined) {
          evaluations.push(failedEvaluation(track, afterEvaluationFailure.replace('before', 'during')))
        } else {
          evaluations.push(evaluation)
        }
      } catch (error) {
        evaluations.push(failedEvaluation(track, `could not evaluate report ${track.report}: ${(error as Error).message}`))
      }
    }

    if (runtime.now() >= deadlines.hardDeadlineAt) suiteDeadlineBreached = true

    console.log('runs:')
    for (const run of runs) console.log(formatTrackRun(run))
    console.log('budget:')
    for (const evaluation of evaluations) {
      for (const line of formatEvaluation(evaluation)) console.log(line)
    }
    const summary = summarize(runs, evaluations, suiteDeadlineBreached, suiteElapsed)
    console.log(formatSummary(summary, suiteDeadlineMs))
    const runFailed = suiteDeadlineBreached || runs.some((run) => !run.cancelled && !isLaneSuccessful(run))
    const budgetFailed = evaluations.some((e) => !e.passed)
    const firstFailedRun = runs.find((run) => !isLaneSuccessful(run))
    const firstFailedEvaluation = evaluations.find((evaluation) => !evaluation.passed)
    const evidenceWritten = writeJsonEvidence(artifactRoot, 'summary.json', {
      schemaVersion: 1,
      sourceRevision: canonicalRun?.sourceRevision,
      suiteStart,
      hardDeadlineAt: deadlines.hardDeadlineAt,
      executionDeadlineAt: deadlines.executionDeadlineAt,
      suiteElapsedMs: suiteElapsed,
      suiteDeadlineBreached,
      passed: !runFailed && !budgetFailed,
      firstFailure: schedulerFailureLaneId !== undefined
        ? { kind: 'lane', laneId: schedulerFailureLaneId }
        : firstFailedRun !== undefined
          ? { kind: 'lane', laneId: firstFailedRun.trackId, error: firstFailedRun.reportError }
          : firstFailedEvaluation !== undefined
            ? { kind: 'report', trackId: firstFailedEvaluation.trackId, error: firstFailedEvaluation.reportError }
            : undefined,
      summary,
      runs,
      evaluations,
    })
    externalAbort?.removeEventListener('abort', abortFromCanonical)
    return !evidenceWritten || runFailed || budgetFailed ? 1 : 0
  }

  for (const track of selected) {
    evaluations.push(evaluateFromPlans(
      track,
      plansByPolicy.get(track.id) ?? [],
      new Map(),
      runtime,
    ))
  }
  console.log('budget:')
  for (const evaluation of evaluations) {
    for (const line of formatEvaluation(evaluation)) console.log(line)
  }
  const summary = summarize(runs, evaluations)
  console.log(formatSummary(summary, suiteDeadlineMs))

  const runFailed = runs.some((run) => !run.cancelled && !isLaneSuccessful(run))
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
