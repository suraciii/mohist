import { execFileSync, spawn } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'
import {
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs'
import { dirname, resolve, basename, isAbsolute, join, relative, sep } from 'node:path'
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
import { formatGuardOutput, summarize } from './diagnostics.js'
import {
  externalAbortCleanupDeadlineAt,
  runWithDeadline,
  suiteDeadlines,
  suiteDeadlinesAt,
  type SuiteDeadlines,
} from './deadline.js'
import { planIdentity, selectApplicationTracks, selectRepositoryTracks, validatePlan } from './plan.js'
import {
  buildLedgerEnvironment,
  createExecutionRunId,
  parseExecutionLedger,
  parseExecutionProvenance,
  readCurrentExecutionIdentity,
  serializeExecutionProvenance,
  validateCurrentExecutionIdentity,
  validateExecutionEvidence,
} from './execution-ledger.js'
import { parseReport } from './reports.js'
import { parseAssemblyName, resolveApphostPath, resolveDiscoveryCommand, resolveFocusedCommand } from './focused.js'
import { nativeProcessTreeOps, terminateProcessTree, type ProcessTreeOps } from './process-tree.js'
import { scheduleLanes, type LaneSpec, type RunningLane } from './scheduler.js'
import { resolveSpawnCommand } from './spawn-command.js'
import { nativeTimeSource } from './time.js'
import type {
  CurrentExecutionIdentity,
  ExecutionLedgerExpectation,
  SuiteConfig,
  TrackConfig,
  TrackEvaluation,
  TrackRun,
} from './types.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

export const DEFAULT_XUNIT_PARALLELISM =
  'xunit-v3:parallel=collections;parallelAlgorithm=conservative;maxThreads=default'

export interface ExecutionProvenanceWriter {
  readonly ensureDirectory: (path: string) => void
  readonly writeText: (path: string, content: string) => void
}

const physicalExecutionProvenanceWriter: ExecutionProvenanceWriter = {
  ensureDirectory: (path) => mkdirSync(path, { recursive: true }),
  writeText: (path, content) => writeFileSync(path, content, 'utf8'),
}

export function writeExecutionProvenance(
  path: string,
  expectation: ExecutionLedgerExpectation,
  writer: ExecutionProvenanceWriter = physicalExecutionProvenanceWriter,
): void {
  writer.ensureDirectory(dirname(path))
  writer.writeText(path, serializeExecutionProvenance(expectation))
}

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
  const assemblyName = track.apphost
    ? undefined
    : (parseAssemblyName(xml) ?? basename(csprojAbs).replace(/\.csproj$/, ''))
  return resolveApphostExecutable(
    resolve(csprojDir, resolveApphostPath({ csprojXml: xml, projectDir: csprojDir, assemblyName })),
  )
}

function resolveApphostExecutable(path: string): string {
  return process.platform === 'win32' && !path.toLowerCase().endsWith('.exe') ? `${path}.exe` : path
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
  const files = [...new Set(roots.flatMap(sourceFiles))].sort((left, right) =>
    left < right ? -1 : left > right ? 1 : 0,
  )
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

function optionValue(args: readonly string[], option: string, fallback: string): string {
  const indexes = args.flatMap((value, index) => (value === option ? [index] : []))
  if (indexes.length > 1) throw new Error(`duplicate xUnit option ${option}`)
  if (indexes.length === 0) return fallback
  const value = args[indexes[0] + 1]
  if (!value || value.startsWith('-')) throw new Error(`xUnit option ${option} requires a value`)
  return value
}

export function parallelismFor(track: TrackConfig): string {
  const args = track.apphostArgs ?? []
  const parallel = optionValue(args, '-parallel', 'collections')
  const parallelAlgorithm = optionValue(args, '-parallelAlgorithm', 'conservative')
  const maxThreads = optionValue(args, '-maxThreads', 'default')
  return `xunit-v3:parallel=${parallel};parallelAlgorithm=${parallelAlgorithm};maxThreads=${maxThreads}`
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
  const apphost = resolveApphostExecutable(
    resolve(projectDir, resolveApphostPath({ csprojXml: xml, projectDir, assemblyName })),
  )
  return {
    assemblyPath: assemblyPathFor(apphost),
    discovery: resolveDiscoveryCommand({ csprojXml: xml, projectDir, assemblyName }),
  }
}

export function commandFor(
  track: TrackConfig,
  reportRoot: string = repoRoot,
): { command: string; args: readonly string[] } {
  if (track.kind === 'dotnet-apphost') {
    const apphost = apphostFor(track)
    const reporterArgs = track.executionLedger
      ? ['-noAutoReporters', '-reporter', 'mohist-ledger']
      : ['-noAutoReporters']
    return {
      command: apphost,
      args: [
        '-noColor',
        '-noLogo',
        ...reporterArgs,
        '-trx',
        resolve(reportRoot, track.report),
        ...(track.apphostArgs ?? []),
      ],
    }
  }
  if (track.kind === 'dotnet-vstest') {
    if (!track.csproj) throw new Error(`track "${track.id}": dotnet-vstest needs csproj`)
    const reportDir = resolve(reportRoot, dirname(track.report))
    const logName = `${track.id}.trx`
    return {
      command: 'dotnet',
      args: [
        'test',
        resolve(repoRoot, track.csproj),
        '--no-build',
        '--no-restore',
        '--logger',
        `trx;LogFileName=${logName}`,
        '--results-directory',
        reportDir,
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

export interface GuardRuntime {
  readonly now: () => number
  readonly timeoutScheduler?: TimeoutScheduler
  readonly processTreeOps?: ProcessTreeOps
  readonly abortSignal?: AbortSignal
}

const nativeGuardRuntime: GuardRuntime = {
  now: nativeTimeSource.now,
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
  output: 'inherit' | 'capture' = 'inherit',
): SpawnedChild {
  const resolvedCommand = resolveSpawnCommand(command, args)
  const detached = process.platform !== 'win32'
  const child = spawn(resolvedCommand.command, resolvedCommand.args as string[], {
    cwd: repoRoot,
    env,
    stdio: output === 'capture' ? ['ignore', 'pipe', 'inherit'] : ['ignore', 'inherit', 'inherit'],
    detached,
  })
  let stdoutText = ''
  child.stdout?.on('data', (chunk: Buffer) => (stdoutText += chunk.toString()))
  const done = new Promise<{ exitCode: number | null; stdout: string }>((resolvePromise) => {
    let settled = false
    const settle = (code: number | null) => {
      if (settled) return
      settled = true
      resolvePromise({ exitCode: code, stdout: stdoutText })
    }
    child.once('error', (error) => {
      process.stderr.write(`could not start ${resolvedCommand.command}: ${error.message}\n`)
      settle(1)
    })
    child.once('close', (code) => {
      settle(code)
    })
  })
  return { done, pid: child.pid ?? -1 }
}

export async function runProcessWithDeadline<TimeoutReason>(input: {
  readonly child: SpawnedChild
  readonly timeout: Promise<TimeoutReason>
  readonly kill: () => Promise<void>
  readonly now: () => number
  readonly hardDeadlineAt?: number
}): Promise<
  Awaited<SpawnedChild['done']> & {
    readonly status: 'passed' | 'failed' | 'timeout'
    readonly elapsedMs: number
    readonly timeoutReason?: TimeoutReason
  }
> {
  const outcome = await runWithDeadline({
    start: () => input.child.done,
    kill: input.kill,
    timeout: input.timeout,
    now: input.now,
    hardDeadlineAt: input.hardDeadlineAt,
  })
  if (outcome.status === 'timeout') return { ...outcome, stdout: '' }
  const completed = await input.child.done
  return { ...outcome, stdout: completed.stdout }
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
  readonly environment: NodeJS.ProcessEnv
}

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
  return {
    tempDir,
    ipcDir,
    homeDir,
    databasePath,
    otelDatabasePath,
    environment: {
      ...inherited,
      TMPDIR: tempDir,
      TEMP: tempDir,
      TMP: tempDir,
      XDG_RUNTIME_DIR: ipcDir,
      HOME: homeDir,
      USERPROFILE: homeDir,
      MOHIST_TEST_LANE: laneId,
      Logging__LogLevel__Default: inherited.Logging__LogLevel__Default ?? 'Warning',
      DOTNET_NOLOGO: inherited.DOTNET_NOLOGO ?? 'true',
      DOTNET_CLI_TELEMETRY_OPTOUT: inherited.DOTNET_CLI_TELEMETRY_OPTOUT ?? 'true',
      DOTNET_GENERATE_ASPNET_CERTIFICATE: inherited.DOTNET_GENERATE_ASPNET_CERTIFICATE ?? 'false',
      ...(isolateServerRuntime
        ? {
            // Only concurrent Spec lanes need isolated server-owned paths.
            // TestServer and the in-memory Orleans transport own the logical
            // HTTP/OTel identities; no OS listener is opened by this lane.
            MOHIST_DB_PATH: databasePath,
            MOHIST_OTEL_DB_PATH: otelDatabasePath,
            MOHIST__Otel__DbPath: otelDatabasePath,
            MOHIST__Otel__BindHost: 'localhost',
            MOHIST__Otel__Port: '0',
            MOHIST__Otel__Endpoint: 'http://localhost/otel',
            OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost',
          }
        : {}),
    },
  }
}

export function isLaneSuccessful(run: TrackRun): boolean {
  return (
    !run.cancelled &&
    !run.timedOut &&
    run.exitCode === 0 &&
    run.reportReady &&
    run.executionLedgerReady !== false &&
    run.cleanupComplete
  )
}

interface PlannedLane {
  readonly lane: LaneSpec
  readonly policyTrack?: TrackConfig
  readonly executionTrack?: TrackConfig
  readonly reportPath?: string
  readonly sandboxOrdinal: number
  readonly deadlineMs: number
}

function laneResources(track: TrackConfig): string[] {
  return [
    'host',
    track.kind === 'vitest' ? 'node' : 'dotnet',
    ...(track.resources ?? []),
    ...(track.id === 'server-spec' ? ['server-spec'] : []),
  ]
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

  const measurementGroups: Array<{
    readonly trackId: string
    readonly executionLaneIds: readonly string[]
    readonly terminalLaneIds: readonly string[]
  }> = []
  for (const trackId of durationMeasurementTracks) {
    const matching = planned.filter((plan) => plan.policyTrack?.id === trackId)
    if (matching.length === 0) return [...planned]
    const coverage = planned.find((plan) => plan.lane.id === `${trackId}-coverage`)
    if (matching.length > 1 && coverage === undefined) return [...planned]
    measurementGroups.push({
      trackId,
      executionLaneIds: matching.map((plan) => plan.lane.id),
      terminalLaneIds: coverage === undefined ? [matching[0].lane.id] : [coverage.lane.id],
    })
  }

  const finalMeasurementLaneIds = measurementGroups.at(-1)!.terminalLaneIds
  const isolationLaneId =
    durationIsolationTrack === undefined
      ? undefined
      : planned.find((plan) => plan.policyTrack?.id === durationIsolationTrack)?.lane.id
  return planned.map((plan) => {
    const measurementIndex = measurementGroups.findIndex(
      (group) => group.executionLaneIds.includes(plan.lane.id) || group.terminalLaneIds.includes(plan.lane.id),
    )
    if (measurementIndex >= 0) {
      const predecessor = measurementIndex === 0 ? [] : measurementGroups[measurementIndex - 1].terminalLaneIds
      const resources =
        measurementGroups[measurementIndex].executionLaneIds.length === 1 ? ['duration-measurement'] : []
      return withLaneConstraints(plan, predecessor, resources)
    }
    const dependencies =
      isolationLaneId !== undefined && plan.lane.id !== isolationLaneId && plan.policyTrack?.kind === 'vitest'
        ? [isolationLaneId]
        : finalMeasurementLaneIds
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
    planned.push({
      lane: { id: track.id, resources: laneResources(track) },
      policyTrack: track,
      executionTrack: track,
      reportPath: resolve(artifactRoot, track.report),
      sandboxOrdinal: planned.length,
      deadlineMs: track.deadlineMs,
    })
  }
  return applyDurationMeasurementPhase(planned, durationMeasurementTracks, durationIsolationTrack)
}

function failedRun(plan: PlannedLane, command: string, reportError: string): TrackRun {
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
  }
}

function cancelledRun(plan: PlannedLane, suiteExpired: boolean, failureLaneId?: string): TrackRun {
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

function startLane(
  plan: PlannedLane,
  graceMs: number,
  suiteDeadline: Promise<void>,
  deadlines: SuiteDeadlines,
  artifactRoot: string,
  runtime: GuardRuntime,
  cancellationDeadlineAt: () => number,
): RunningLane<TrackRun> {
  const trackDeadline = createTimeout(plan.deadlineMs, runtime.timeoutScheduler)
  const laneStartedAt = runtime.now()
  let cleanupComplete = true
  let currentChild: SpawnedChild | undefined
  let cancellationRequested = false
  let cancellation: Promise<void> | undefined
  let resolveLaneCancellation!: () => void
  const laneCancellation = new Promise<void>((resolvePromise) => {
    resolveLaneCancellation = resolvePromise
  })
  const cancel = () => {
    if (!cancellationRequested) {
      cancellationRequested = true
      resolveLaneCancellation()
    }
    cancellation ??=
      currentChild === undefined
        ? Promise.resolve()
        : killTree(currentChild, graceMs, cancellationDeadlineAt(), {
            ...(runtime.processTreeOps ?? nativeProcessTreeOps),
            now: runtime.now,
          }).then((completed) => {
            cleanupComplete = cleanupComplete && completed
          })
    return cancellation
  }

  const deadline = Promise.race([
    trackDeadline.promise.then(() => 'track' as const),
    suiteDeadline.then(() => 'suite' as const),
    laneCancellation.then(() => 'suite' as const),
  ])

  const runStage = async (
    command: string,
    args: readonly string[],
    environment: NodeJS.ProcessEnv,
    output: 'inherit' | 'capture' = 'inherit',
  ) => {
    if (cancellationRequested) {
      return {
        status: 'timeout' as const,
        exitCode: null,
        elapsedMs: runtime.now() - laneStartedAt,
        timeoutReason: 'suite' as const,
        stdout: '',
      }
    }
    const child = spawnChild(command, args, environment, output)
    currentChild = child
    const stageResult = await runProcessWithDeadline({
      child,
      timeout: deadline,
      kill: async () => {
        const completed = await killTree(child, graceMs, cancellationDeadlineAt(), {
          ...(runtime.processTreeOps ?? nativeProcessTreeOps),
          now: runtime.now,
        })
        cleanupComplete = cleanupComplete && completed
      },
      now: runtime.now,
      hardDeadlineAt: deadlines.hardDeadlineAt,
    })
    if (stageResult.status !== 'timeout') {
      const completed = await killTree(child, graceMs, cancellationDeadlineAt(), {
        ...(runtime.processTreeOps ?? nativeProcessTreeOps),
        now: runtime.now,
      })
      cleanupComplete = cleanupComplete && completed
    }
    if (currentChild === child) currentChild = undefined
    return stageResult
  }

  const result = (async (): Promise<TrackRun> => {
    let command = 'not started'
    let args: readonly string[] = []
    let ledgerExpectation: ExecutionLedgerExpectation | undefined
    let ledgerPath: string | undefined
    let provenancePath: string | undefined
    let executionEnvironment: Readonly<Record<string, string>> | undefined
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

      if (plan.reportPath) prepareReportTarget(plan.reportPath)
      const executionTrack = plan.executionTrack
      if (executionTrack?.executionLedger) {
        if (!executionTrack.executionProvenance || !executionTrack.executionSourceRoots?.length) {
          throw new Error(`track "${executionTrack.id}" has incomplete execution ledger configuration`)
        }
        ledgerPath = resolve(artifactRoot, executionTrack.executionLedger)
        provenancePath = resolve(artifactRoot, executionTrack.executionProvenance)
        const artifactPaths = [plan.reportPath, ledgerPath, provenancePath].filter(
          (path): path is string => path !== undefined,
        )
        if (new Set(artifactPaths).size !== artifactPaths.length) {
          throw new Error('TRX report, execution ledger, and execution provenance paths must differ')
        }
        prepareReportTarget(ledgerPath)
        prepareReportTarget(provenancePath)
        const ledgerPlan = executionLedgerPlan(executionTrack)
        const currentIdentity = await readCurrentExecutionIdentity(
          {
            assemblyPath: ledgerPlan.assemblyPath,
            sourceRoots: executionTrack.executionSourceRoots,
            parallelism: parallelismFor(executionTrack),
          },
          {
            readAssemblySha256: sha256File,
            readSourceSha256: sha256Sources,
            readDiscovery: async () => {
              const discovery = await runStage(
                ledgerPlan.discovery.apphost,
                ledgerPlan.discovery.args,
                sandbox.environment,
                'capture',
              )
              if (discovery.status !== 'passed') {
                const reason =
                  discovery.status === 'timeout'
                    ? 'compiled discovery exceeded the track or suite deadline'
                    : `compiled discovery failed with exit ${discovery.exitCode}`
                throw new Error(reason)
              }
              return discovery.stdout
            },
          },
        )
        ledgerExpectation = {
          runId: createExecutionRunId({ now: runtime.now }, randomUUID),
          ...currentIdentity,
        }
        writeExecutionProvenance(provenancePath, ledgerExpectation)
        executionEnvironment = buildLedgerEnvironment({ ...ledgerExpectation, ledgerPath })
      }

      if (cancellationRequested) throw new Error('lane was cancelled before test execution')
      if (executionTrack) {
        ;({ command, args } = commandFor(executionTrack, artifactRoot))
      } else {
        throw new Error(`lane "${plan.lane.id}" has no execution track`)
      }

      const outcome = await runStage(command, args, { ...sandbox.environment, ...executionEnvironment })
      let reportReady = false
      try {
        reportReady = plan.reportPath ? reportFileReady(plan.reportPath) : outcome.status === 'passed'
      } catch {
        reportReady = false
      }
      let executionLedgerReady: boolean | undefined
      if (ledgerPath) {
        try {
          executionLedgerReady = reportFileReady(ledgerPath)
        } catch {
          executionLedgerReady = false
        }
      }
      const reportError = !cleanupComplete
        ? `lane process tree did not reach a terminal state before ${plan.lane.id} cancellation completed`
        : !reportReady
          ? plan.reportPath
            ? `report ${plan.reportPath} was not created or refreshed by the lane`
            : 'coverage verification did not complete'
          : executionLedgerReady === false
            ? `execution ledger ${ledgerPath} was not created or refreshed by the lane`
            : undefined
      return {
        trackId: plan.lane.id,
        policyTrackId: plan.policyTrack?.id,
        reportPath: plan.reportPath,
        timedOut: outcome.status === 'timeout',
        timeoutReason: outcome.timeoutReason,
        exitCode: outcome.exitCode,
        elapsedMs: runtime.now() - laneStartedAt,
        deadlineMs: plan.deadlineMs,
        command: `${command} ${args.join(' ')}`,
        reportReady,
        cleanupComplete,
        reportError,
        executionLedgerReady,
        executionLedgerError: executionLedgerReady === false ? reportError : undefined,
        executionLedgerExpectation: ledgerExpectation,
      }
    } catch (error) {
      return {
        ...failedRun(plan, command, `could not prepare or execute lane: ${(error as Error).message}`),
        elapsedMs: runtime.now() - laneStartedAt,
        cleanupComplete,
        executionLedgerReady: ledgerPath ? false : undefined,
        executionLedgerError: ledgerPath ? (error as Error).message : undefined,
        executionLedgerExpectation: ledgerExpectation,
      }
    }
  })().finally(trackDeadline.cancel)
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

export interface TrackArtifactReader {
  readonly readText: (path: string) => string
}

export function evaluateTrackArtifacts(
  track: TrackConfig,
  artifacts: TrackArtifactReader,
  run?: TrackRun,
  currentIdentity?: CurrentExecutionIdentity,
): TrackEvaluation {
  if (run?.cancelled) {
    const state = run.reportReady
      ? 'report is ignored after cancellation'
      : (run.reportError ?? 'report was not produced')
    return failedEvaluation(track, `lane ${run.trackId} was cancelled ${run.cancellationReason ?? ''}; ${state}`)
  }
  if (run && !run.reportReady) {
    return failedEvaluation(track, run.reportError ?? `report ${track.report} was not refreshed`)
  }
  if (track.executionLedger && run && !run.executionLedgerReady) {
    return failedEvaluation(
      track,
      run.executionLedgerError ?? `execution ledger ${track.executionLedger} was not refreshed`,
    )
  }
  try {
    const trxCases = parseReport(track.reportFormat, artifacts.readText(track.report))
    if (track.executionLedger) {
      if (!track.executionProvenance) return failedEvaluation(track, 'execution provenance path is not configured')
      const expected = parseExecutionProvenance(artifacts.readText(track.executionProvenance))
      if (run?.executionLedgerExpectation) {
        if (serializeExecutionProvenance(run.executionLedgerExpectation) !== serializeExecutionProvenance(expected)) {
          return failedEvaluation(track, 'saved execution provenance does not match the current run')
        }
      } else {
        if (!currentIdentity)
          return failedEvaluation(track, 'current execution identity was not captured for saved evidence')
        const identityErrors = validateCurrentExecutionIdentity(expected, currentIdentity)
        if (identityErrors.length > 0) {
          return failedEvaluation(track, `saved execution provenance is stale: ${identityErrors.join('; ')}`)
        }
      }
      const parsedLedger = parseExecutionLedger(artifacts.readText(track.executionLedger))
      const evidence = validateExecutionEvidence(trxCases, parsedLedger, expected)
      if (evidence.errors.length > 0) {
        return failedEvaluation(track, `execution ledger contract failed: ${evidence.errors.join('; ')}`)
      }
      return evaluateTrack(track, evidence.cases)
    }
    return evaluateTrack(track, trxCases)
  } catch (error) {
    return failedEvaluation(track, `could not read report ${track.report}: ${(error as Error).message}`)
  }
}

function evaluateFromPlans(
  track: TrackConfig,
  plans: readonly PlannedLane[],
  runsByLane: ReadonlyMap<string, TrackRun>,
  artifactRoot: string,
  currentIdentity?: CurrentExecutionIdentity,
): TrackEvaluation {
  if (track.executionLedger) {
    if (plans.length !== 1 || !plans[0].reportPath) {
      return failedEvaluation(track, 'execution ledger track must map to exactly one report lane')
    }
    const plan = plans[0]
    const run = runsByLane.get(plan.lane.id)
    return evaluateTrackArtifacts(
      track,
      {
        readText: (path) =>
          readFileSync(path === track.report ? plan.reportPath! : resolve(artifactRoot, path), 'utf8'),
      },
      run,
      currentIdentity,
    )
  }
  const cases = []
  for (const plan of plans) {
    if (!plan.reportPath) continue
    const run = runsByLane.get(plan.lane.id)
    if (run?.cancelled) {
      const state = run.reportReady
        ? 'report is ignored after cancellation'
        : (run.reportError ?? 'report was not produced')
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
  return evaluateTrack(track, cases)
}

async function readSavedTrackIdentity(
  track: TrackConfig,
  artifactRoot: string,
  graceMs: number,
  deadlines: SuiteDeadlines,
  runtime: GuardRuntime,
): Promise<CurrentExecutionIdentity> {
  if (!track.executionSourceRoots?.length) {
    throw new Error(`track "${track.id}" requires executionSourceRoots for execution ledger evidence`)
  }
  const plan = executionLedgerPlan(track)
  return readCurrentExecutionIdentity(
    {
      assemblyPath: plan.assemblyPath,
      sourceRoots: track.executionSourceRoots,
      parallelism: parallelismFor(track),
    },
    {
      readAssemblySha256: sha256File,
      readSourceSha256: sha256Sources,
      readDiscovery: async () => {
        const child = spawnChild(plan.discovery.apphost, plan.discovery.args, process.env, 'capture')
        const remaining = Math.max(0, deadlines.executionDeadlineAt - runtime.now())
        const timer = createTimeout(Math.min(track.deadlineMs, remaining), runtime.timeoutScheduler)
        try {
          const result = await runProcessWithDeadline({
            child,
            timeout: timer.promise.then(() => 'suite' as const),
            kill: async () => {
              await killTree(child, graceMs, cleanupDeadlineAt(runtime.now(), deadlines.hardDeadlineAt, graceMs), {
                ...(runtime.processTreeOps ?? nativeProcessTreeOps),
                now: runtime.now,
              })
            },
            now: runtime.now,
            hardDeadlineAt: deadlines.hardDeadlineAt,
          })
          if (result.status !== 'passed') {
            throw new Error(
              result.status === 'timeout'
                ? 'compiled discovery exceeded the suite deadline'
                : `compiled discovery failed with exit ${result.exitCode}`,
            )
          }
          const cleanupComplete = await killTree(
            child,
            graceMs,
            cleanupDeadlineAt(runtime.now(), deadlines.hardDeadlineAt, graceMs),
            { ...(runtime.processTreeOps ?? nativeProcessTreeOps), now: runtime.now },
          )
          if (!cleanupComplete) throw new Error('compiled discovery process tree did not reach a terminal state')
          return result.stdout
        } finally {
          timer.cancel()
        }
      },
    },
  )
}

function focusedFlow(csprojPath: string, className: string): number {
  try {
    const xml = readCsproj(csprojPath)
    const assemblyName = parseAssemblyName(xml) ?? basename(csprojPath).replace(/\.csproj$/, '')
    const cmd = resolveFocusedCommand({ csprojXml: xml, className, projectDir: dirname(csprojPath), assemblyName })
    const list = execFileSync(cmd.apphost, cmd.verify as string[], { cwd: repoRoot, encoding: 'utf8' })
    const classes = list
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean)
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
  application?: string
  repository: boolean
  all: boolean
  artifactRoot?: string
  runRoot?: string
  suiteDeadlineMs?: number
  suiteDeadlineAtMs?: number
  requireBuildStamp: boolean
  requireEnforced: boolean
  focused?: { csproj: string; className: string }
}

export function parseArgs(argv: readonly string[]): Args {
  const tracks: string[] = []
  let mode: 'run' | 'check' | 'focused' = 'run'
  let application: string | undefined
  let repository = false
  let all = false
  let artifactRoot: string | undefined
  let runRoot: string | undefined
  let suiteDeadlineMs: number | undefined
  let suiteDeadlineAtMs: number | undefined
  let requireBuildStamp = false
  let requireEnforced = false
  let focused: { csproj: string; className: string } | undefined
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--check') mode = 'check'
    else if (arg === '--all') all = true
    else if (arg === '--track') tracks.push(argv[++i])
    else if (arg.startsWith('--track=')) tracks.push(arg.slice('--track='.length))
    else if (arg === '--application') application = argv[++i] ?? ''
    else if (arg.startsWith('--application=')) application = arg.slice('--application='.length)
    else if (arg === '--repository') repository = true
    else if (arg === '--artifact-root') artifactRoot = argv[++i]
    else if (arg.startsWith('--artifact-root=')) artifactRoot = arg.slice('--artifact-root='.length)
    else if (arg === '--run-root') runRoot = argv[++i]
    else if (arg.startsWith('--run-root=')) runRoot = arg.slice('--run-root='.length)
    else if (arg === '--suite-deadline-ms') suiteDeadlineMs = Number(argv[++i])
    else if (arg.startsWith('--suite-deadline-ms=')) suiteDeadlineMs = Number(arg.slice('--suite-deadline-ms='.length))
    else if (arg === '--suite-deadline-at-ms') suiteDeadlineAtMs = Number(argv[++i])
    else if (arg.startsWith('--suite-deadline-at-ms='))
      suiteDeadlineAtMs = Number(arg.slice('--suite-deadline-at-ms='.length))
    else if (arg === '--require-build-stamp') requireBuildStamp = true
    else if (arg === '--require-enforced') requireEnforced = true
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
  return {
    mode,
    tracks,
    application,
    repository,
    all,
    artifactRoot,
    runRoot,
    suiteDeadlineMs,
    suiteDeadlineAtMs,
    requireBuildStamp,
    requireEnforced,
    focused,
  }
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
    application,
    repository,
    all,
    artifactRoot: artifactRootArg,
    runRoot: runRootArg,
    suiteDeadlineMs: requestedDeadlineMs,
    suiteDeadlineAtMs: requestedDeadlineAtMs,
    requireBuildStamp,
    requireEnforced,
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
  const errors = [...validateConfig(config), ...validatePlan(config)]
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
  if (
    application === '' ||
    (application !== undefined && repository) ||
    (application !== undefined && tracks.length > 0) ||
    (repository && tracks.length > 0)
  ) {
    process.stderr.write('--application, --repository, and --track are mutually exclusive scopes\n')
    return 2
  }

  let selected: readonly TrackConfig[]
  if (application !== undefined) {
    try {
      selected = selectApplicationTracks(config, application).tracks
    } catch (error) {
      process.stderr.write(`${(error as Error).message}\n`)
      return 2
    }
  } else if (repository) {
    try {
      selected = selectRepositoryTracks(config).tracks
    } catch (error) {
      process.stderr.write(`${(error as Error).message}\n`)
      return 2
    }
  } else if (tracks.length > 0) {
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
  const canonicalRun =
    mode === 'run' && suppliedRunRoot !== undefined ? readCanonicalRunMetadata(suppliedRunRoot) : undefined
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
  const deadlines =
    requestedDeadlineAtMs === undefined
      ? suiteDeadlines(
          suiteStart,
          Math.min(config.suiteDeadlineMs, requestedDeadlineMs ?? config.suiteDeadlineMs),
          graceMs,
        )
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
      const artifactParent =
        artifactRootArg === undefined ? undefined : externalAbsolutePath(artifactRootArg, '--artifact-root')
      const runId = `${suiteStart}-${process.pid}`
      artifactRoot = createUniqueArtifactRoot(runId, repoRoot, artifactParent)
      writeFileSync(
        resolve(artifactRoot, 'run.json'),
        JSON.stringify({ runId, startedAt: suiteStart, suiteDeadlineMs }, null, 2) + '\n',
      )
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

  if (
    mode === 'run' &&
    !writeJsonEvidence(artifactRoot, 'plan.json', {
      sourceRevision: canonicalRun?.sourceRevision,
      planIdentity: planIdentity(config),
      suiteStart,
      hardDeadlineAt: deadlines.hardDeadlineAt,
      executionDeadlineAt: deadlines.executionDeadlineAt,
      selectedTracks: selected.map((track) => track.id),
      lanes: planned.map((plan) => ({
        id: plan.lane.id,
        policyTrackId: plan.policyTrack?.id,
        dependsOn: plan.lane.dependsOn ?? [],
        resources: plan.lane.resources ?? [],
        reportPath: plan.reportPath,
        sandboxOrdinal: plan.sandboxOrdinal,
        deadlineMs: plan.deadlineMs,
      })),
    })
  )
    return 1

  const runs: TrackRun[] = []
  const evaluations: TrackEvaluation[] = []

  if (mode === 'run') {
    let suiteExpired = false
    let suiteAbortReason: 'deadline' | 'external' | undefined
    let externalCleanupDeadline: number | undefined
    let schedulerFailureLaneId: string | undefined
    const suiteAbort = new AbortController()
    let resolveSuiteDeadline!: () => void
    const suiteDeadline = new Promise<void>((resolvePromise) => {
      resolveSuiteDeadline = resolvePromise
    })
    const expireSuite = (reason: 'deadline' | 'external') => {
      if (suiteExpired) return
      suiteExpired = true
      suiteAbortReason = reason
      if (reason === 'external') {
        externalCleanupDeadline = externalAbortCleanupDeadlineAt(runtime.now(), deadlines.hardDeadlineAt, graceMs)
      }
      suiteAbort.abort()
      resolveSuiteDeadline()
    }
    const externalAbort = runtime.abortSignal
    const abortFromCanonical = () => expireSuite('external')
    if (externalAbort?.aborted) abortFromCanonical()
    else externalAbort?.addEventListener('abort', abortFromCanonical, { once: true })
    const executionNow = runtime.now()
    const executionTimer =
      executionNow >= deadlines.executionDeadlineAt
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
        (lane) =>
          startLane(
            lanesById.get(lane.id)!,
            graceMs,
            suiteDeadline,
            deadlines,
            artifactRoot,
            runtime,
            () => externalCleanupDeadline ?? cleanupDeadlineAt(runtime.now(), deadlines.hardDeadlineAt, graceMs),
          ),
        isLaneSuccessful,
        { resourceLimits, abort: suiteAbort.signal },
      )
      schedulerFailureLaneId = scheduled.failureLaneId
      for (const scheduledLane of scheduled.lanes) {
        const plan = lanesById.get(scheduledLane.lane.id)!
        const result =
          scheduledLane.result ??
          (scheduledLane.state === 'failed'
            ? failedRun(plan, 'scheduler', 'lane execution rejected before producing a report')
            : cancelledRun(plan, suiteExpired, scheduled.failureLaneId))
        const run =
          scheduledLane.state === 'cancelled' && !result.cancelled
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
      }
    } finally {
      executionTimer?.cancel()
    }
    const suiteElapsed = runtime.now() - suiteStart
    let suiteDeadlineBreached = suiteExpired || runtime.now() >= deadlines.hardDeadlineAt
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
        const evaluation = evaluateFromPlans(track, plansByPolicy.get(track.id) ?? [], runsByTrack, artifactRoot)
        const afterEvaluationFailure = reportEvaluationFailureReason(
          runtime.now(),
          deadlines,
          suiteAbortReason === 'external',
        )
        if (afterEvaluationFailure !== undefined) {
          evaluations.push(failedEvaluation(track, afterEvaluationFailure.replace('before', 'during')))
        } else {
          evaluations.push(
            requireEnforced && !track.enforce
              ? {
                  ...evaluation,
                  passed: false,
                  reportError: evaluation.reportError ?? 'track is baseline-pending and is not enforced',
                }
              : evaluation,
          )
        }
      } catch (error) {
        evaluations.push(
          failedEvaluation(track, `could not evaluate report ${track.report}: ${(error as Error).message}`),
        )
      }
    }

    if (runtime.now() >= deadlines.hardDeadlineAt) suiteDeadlineBreached = true

    const summary = summarize(runs, evaluations, suiteDeadlineBreached, suiteElapsed)
    const runFailed = suiteDeadlineBreached || runs.some((run) => !run.cancelled && !isLaneSuccessful(run))
    const budgetFailed = evaluations.some((e) => !e.passed)
    const firstFailedRun = runs.find((run) => !isLaneSuccessful(run))
    const firstFailedEvaluation = evaluations.find((evaluation) => !evaluation.passed)
    const evidenceWritten = writeJsonEvidence(artifactRoot, 'summary.json', {
      schemaVersion: 1,
      sourceRevision: canonicalRun?.sourceRevision,
      planIdentity: planIdentity(config),
      suiteStart,
      hardDeadlineAt: deadlines.hardDeadlineAt,
      executionDeadlineAt: deadlines.executionDeadlineAt,
      suiteElapsedMs: suiteElapsed,
      suiteDeadlineBreached,
      passed: !runFailed && !budgetFailed,
      firstFailure:
        schedulerFailureLaneId !== undefined
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
    const passed = evidenceWritten && !runFailed && !budgetFailed
    const output = formatGuardOutput({
      passed,
      summary,
      suiteDeadlineMs,
      failedRuns: runs.filter((run) => !run.cancelled && !isLaneSuccessful(run)),
      failedEvaluations: evaluations.filter((evaluation) => !evaluation.passed),
    })
    if (output) process.stderr.write(`${output}\n`)
    externalAbort?.removeEventListener('abort', abortFromCanonical)
    return passed ? 0 : 1
  }

  for (const track of selected) {
    let currentIdentity: CurrentExecutionIdentity | undefined
    if (track.executionLedger) {
      try {
        currentIdentity = await readSavedTrackIdentity(track, artifactRoot, graceMs, deadlines, runtime)
      } catch (error) {
        evaluations.push(
          failedEvaluation(track, `could not validate current execution identity: ${(error as Error).message}`),
        )
        continue
      }
    }
    const evaluation = evaluateFromPlans(
      track,
      plansByPolicy.get(track.id) ?? [],
      new Map(),
      artifactRoot,
      currentIdentity,
    )
    evaluations.push(
      requireEnforced && !track.enforce
        ? {
            ...evaluation,
            passed: false,
            reportError: evaluation.reportError ?? 'track is baseline-pending and is not enforced',
          }
        : evaluation,
    )
  }
  const summary = summarize(runs, evaluations)
  const runFailed = runs.some((run) => !run.cancelled && !isLaneSuccessful(run))
  const budgetFailed = evaluations.some((e) => !e.passed)
  const passed = !runFailed && !budgetFailed
  const output = formatGuardOutput({
    passed,
    summary,
    suiteDeadlineMs,
    failedRuns: runs.filter((run) => !run.cancelled && !isLaneSuccessful(run)),
    failedEvaluations: evaluations.filter((evaluation) => !evaluation.passed),
  })
  if (output) process.stderr.write(`${output}\n`)
  return passed ? 0 : 1
}

const isMain = process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  void main().then(
    (code) => process.exit(code),
    (error) => {
      console.error(`test-duration: fatal guard error: ${(error as Error).message}`)
      process.exit(1)
    },
  )
}
