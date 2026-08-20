import { execFileSync } from 'node:child_process'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { createArtifactRoot, phaseSucceeded, runPhase, type PhaseResult } from './canonical.js'
import { parseSuiteConfig, validateConfig } from './config.js'
import { suiteDeadlines, type SuiteDeadlines } from './deadline.js'
import { main as runGuard, type GuardRuntime } from './guard.js'
import { createTerminationSignal, runCommandSequence, type ApplicationRuntime } from './application.js'
import { applicationBuilds, planIdentity, selectFastTracks, selectPortfolioTracks, validatePlan } from './plan.js'
import { nativeTimeSource } from './time.js'
import type { SuiteConfig, TrackConfig } from './types.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

export type CommandMode = 'fast' | 'portfolio'

export interface CommandArgs {
  readonly mode?: CommandMode
  readonly help: boolean
}

export function parseArgs(argv: readonly string[]): CommandArgs {
  let mode: CommandMode | undefined
  let help = false
  for (const arg of argv) {
    if (arg === '--help' || arg === '-h') {
      help = true
      continue
    }
    if (arg === 'fast' || arg === 'portfolio') {
      if (mode !== undefined) throw new Error('test command accepts exactly one mode')
      mode = arg
      continue
    }
    throw new Error(`unknown test command argument: ${arg}`)
  }
  return { mode, help }
}

export interface CommandRuntime {
  readonly now: () => number
  readonly pid: () => number
  readonly sourceRevision: () => string
  readonly createArtifactRoot: (runId: string) => string
  readonly writeFile: (path: string, content: string) => void
  readonly runPhase: (
    name: string,
    command: string,
    args: readonly string[],
    artifactRoot: string,
    deadlines: SuiteDeadlines,
    now: () => number,
    abortSignal: AbortSignal,
  ) => Promise<PhaseResult>
  readonly runGuard: (argv: readonly string[], runtime: GuardRuntime) => Promise<number>
  readonly report: (line: string) => void
}

function writeEvidence(runtime: Pick<CommandRuntime, 'writeFile'>, root: string, name: string, value: unknown): void {
  const path = resolve(root, name)
  mkdirSync(dirname(path), { recursive: true })
  runtime.writeFile(path, `${JSON.stringify(value, null, 2)}\n`)
}

function applicationsFor(tracks: readonly TrackConfig[]): readonly string[] {
  return [...new Set(tracks.flatMap((track) => (track.application === undefined ? [] : [track.application])))]
}

function selectedTracks(config: SuiteConfig, mode: CommandMode): readonly TrackConfig[] {
  return mode === 'fast' ? selectFastTracks(config) : selectPortfolioTracks(config)
}

export async function runCommand(
  config: SuiteConfig,
  mode: CommandMode,
  runtime: CommandRuntime,
  artifactRoot: string,
  startedAt: number,
  deadlines: SuiteDeadlines,
  abortSignal: AbortSignal,
  sourceRevision: string,
): Promise<number> {
  const tracks = selectedTracks(config, mode)
  const applications = applicationsFor(tracks)
  const identity = planIdentity(config)
  writeEvidence(runtime, artifactRoot, 'command.json', {
    mode,
    sourceRevision,
    planIdentity: identity,
    startedAt,
    hardDeadlineAt: deadlines.hardDeadlineAt,
    selectedTracks: tracks.map((track) => track.id),
    applications,
  })

  const buildRuntime: Pick<ApplicationRuntime, 'now' | 'runPhase'> = {
    now: runtime.now,
    runPhase: runtime.runPhase,
  }
  const buildResults = await Promise.all(
    applications.map(async (application) => {
      const root = resolve(artifactRoot, 'builds', application)
      const result = await runCommandSequence(
        applicationBuilds(config, application),
        buildRuntime,
        root,
        deadlines,
        abortSignal,
        `build-${application}`,
      )
      return { application, ...result }
    }),
  )
  const buildsPassed = buildResults.every((result) => result.passed)

  let checksPassed = true
  let checkResults: readonly PhaseResult[] = []
  if (mode === 'fast' && config.plan?.fastChecks !== undefined) {
    const checks = await runCommandSequence(
      config.plan.fastChecks,
      buildRuntime,
      resolve(artifactRoot, 'checks'),
      deadlines,
      abortSignal,
      'check',
    )
    checksPassed = checks.passed
    checkResults = checks.results
  }

  let guardCode = 1
  if (buildsPassed && checksPassed && !abortSignal.aborted && runtime.now() < deadlines.hardDeadlineAt) {
    const guardArgs = [
      '--artifact-root',
      artifactRoot,
      '--require-enforced',
      '--suite-deadline-at-ms',
      String(deadlines.hardDeadlineAt),
      ...tracks.flatMap((track) => ['--track', track.id]),
    ]
    guardCode = await runtime.runGuard(guardArgs, { now: runtime.now, abortSignal })
  }

  const passed = buildsPassed && checksPassed && guardCode === 0 && !abortSignal.aborted
  writeEvidence(runtime, artifactRoot, 'command-summary.json', {
    schemaVersion: 1,
    mode,
    sourceRevision,
    planIdentity: identity,
    selectedTracks: tracks.map((track) => track.id),
    builds: buildResults.map((result) => ({
      application: result.application,
      passed: result.passed,
      results: result.results,
    })),
    checks: checkResults,
    guardCode,
    passed,
  })
  return passed ? 0 : 1
}

function nativeSourceRevision(): string {
  return execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repoRoot, encoding: 'utf8' }).trim()
}

function nativeSourceConfig(): SuiteConfig {
  return parseSuiteConfig(readFileSync(resolve(repoRoot, 'test-duration.config.jsonc'), 'utf8'))
}

const nativeRuntime: CommandRuntime = {
  now: nativeTimeSource.now,
  pid: () => process.pid,
  sourceRevision: nativeSourceRevision,
  createArtifactRoot,
  writeFile: (path, content) => writeFileSync(path, content),
  runPhase,
  runGuard,
  report: (line) => console.log(line),
}

export async function main(
  argv: readonly string[] = process.argv.slice(2),
  runtime: CommandRuntime = nativeRuntime,
): Promise<number> {
  let args: CommandArgs
  try {
    args = parseArgs(argv)
  } catch (error) {
    process.stderr.write(`${(error as Error).message}\nusage: npm run test:fast | npm test\n`)
    return 2
  }
  if (args.help) {
    runtime.report('usage: npm run test:fast | npm test')
    return 0
  }
  if (args.mode === undefined) {
    process.stderr.write('a test command mode is required: fast or portfolio\n')
    return 2
  }

  let config: SuiteConfig
  try {
    config = nativeSourceConfig()
  } catch (error) {
    process.stderr.write(`could not read test plan: ${(error as Error).message}\n`)
    return 2
  }
  const configErrors = [...validateConfig(config), ...validatePlan(config)]
  if (configErrors.length > 0) {
    process.stderr.write(`invalid test plan:\n${configErrors.map((error) => `  - ${error}`).join('\n')}\n`)
    return 2
  }

  const startedAt = runtime.now()
  const runId = `${startedAt}-${runtime.pid()}`
  let artifactRoot: string
  let sourceRevision: string
  try {
    artifactRoot = runtime.createArtifactRoot(runId)
    sourceRevision = runtime.sourceRevision()
  } catch (error) {
    process.stderr.write(`could not create test diagnostics: ${(error as Error).message}\n`)
    return 1
  }
  const deadlines = suiteDeadlines(startedAt, config.suiteDeadlineMs, config.killGraceMs ?? 5000)
  const termination = createTerminationSignal()
  try {
    runtime.report(`test command diagnostics: ${artifactRoot}`)
    return await runCommand(
      config,
      args.mode,
      runtime,
      artifactRoot,
      startedAt,
      deadlines,
      termination.signal,
      sourceRevision,
    )
  } catch (error) {
    writeEvidence(runtime, artifactRoot, 'fatal-error.json', {
      message: error instanceof Error ? error.message : String(error),
    })
    process.stderr.write(`test command failed: ${(error as Error).message}\n`)
    return 1
  } finally {
    termination.dispose()
  }
}

const isMain = process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  void main().then(
    (code) => process.exit(code),
    (error) => {
      console.error(`test command fatal error: ${(error as Error).message}`)
      process.exit(1)
    },
  )
}
