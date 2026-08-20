import { execFileSync } from 'node:child_process'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { createArtifactRoot, phaseSucceeded, runPhase, type PhaseResult } from './canonical.js'
import { parseSuiteConfig, validateConfig } from './config.js'
import { suiteDeadlines, type SuiteDeadlines } from './deadline.js'
import { main as runGuard, type GuardRuntime } from './guard.js'
import { applicationBuilds, formatApplicationHelp, planIdentity, validatePlan } from './plan.js'
import { nativeTimeSource } from './time.js'
import type { CommandConfig, SuiteConfig } from './types.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

export interface ApplicationArgs {
  readonly application?: string
  readonly help: boolean
}

export function parseArgs(argv: readonly string[]): ApplicationArgs {
  let application: string | undefined
  let help = false
  for (const arg of argv) {
    if (arg === '--help' || arg === '-h') {
      help = true
      continue
    }
    if (arg.startsWith('-')) throw new Error(`unknown test:app argument: ${arg}`)
    if (application !== undefined) throw new Error('test:app accepts exactly one application')
    application = arg
  }
  return { application, help }
}

export interface ApplicationRuntime {
  readonly now: () => number
  readonly pid: () => number
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

export interface ApplicationExecutionContext {
  readonly runId: string
  readonly startedAt: number
  readonly deadlines: SuiteDeadlines
  readonly artifactRoot: string
  readonly abortSignal: AbortSignal
  readonly sourceRevision?: string
  readonly planIdentity?: string
}

export async function prepareApplicationScope(
  application: string,
  config: SuiteConfig,
  runtime: ApplicationRuntime,
  context: ApplicationExecutionContext,
): Promise<boolean> {
  const { artifactRoot, deadlines, abortSignal } = context
  try {
    const commands = applicationBuilds(config, application)
    mkdirSync(artifactRoot, { recursive: true })
    runtime.report(`test:app ${application} diagnostics: ${artifactRoot}`)
    writeEvidence(runtime, artifactRoot, 'run.json', {
      runId: context.runId,
      startedAt: context.startedAt,
      suiteDeadlineMs: config.suiteDeadlineMs,
      ...(context.sourceRevision ? { sourceRevision: context.sourceRevision } : {}),
      ...(context.planIdentity ? { planIdentity: context.planIdentity } : {}),
    })
    writeEvidence(runtime, artifactRoot, 'application.json', {
      application,
      buildCommands: commands,
      ...(context.sourceRevision ? { sourceRevision: context.sourceRevision } : {}),
      ...(context.planIdentity ? { planIdentity: context.planIdentity } : {}),
    })

    const build = await runCommandSequence(commands, runtime, artifactRoot, deadlines, abortSignal)
    if (!build.passed) {
      writeEvidence(runtime, artifactRoot, 'summary.json', {
        application,
        passed: false,
        phase: 'build',
        build,
      })
      return false
    }
    writeEvidence(runtime, artifactRoot, 'build-stamp.json', {
      runId: context.runId,
      builtAt: runtime.now(),
      ...(context.sourceRevision ? { sourceRevision: context.sourceRevision } : {}),
      ...(context.planIdentity ? { planIdentity: context.planIdentity } : {}),
    })
    return !abortSignal.aborted && runtime.now() < deadlines.hardDeadlineAt
  } catch (error) {
    writeEvidence(runtime, artifactRoot, 'fatal-error.json', {
      message: error instanceof Error ? error.message : String(error),
    })
    process.stderr.write(`test:app failed: ${(error as Error).message}\n`)
    return false
  }
}

export async function runPreparedApplicationScope(
  application: string,
  runtime: ApplicationRuntime,
  context: ApplicationExecutionContext,
): Promise<number> {
  const { artifactRoot, deadlines, abortSignal } = context
  if (abortSignal.aborted || runtime.now() >= deadlines.hardDeadlineAt) return 1
  return await runtime.runGuard(
    [
      '--application',
      application,
      '--run-root',
      artifactRoot,
      '--require-build-stamp',
      '--require-enforced',
      '--suite-deadline-at-ms',
      String(deadlines.hardDeadlineAt),
    ],
    { now: runtime.now, abortSignal },
  )
}

export function createTerminationSignal(): { readonly signal: AbortSignal; readonly dispose: () => void } {
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

function nativeSourceConfig(): SuiteConfig {
  return parseSuiteConfig(readFileSync(resolve(repoRoot, 'test-duration.config.jsonc'), 'utf8'))
}

function nativeSourceRevision(): string {
  return execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repoRoot, encoding: 'utf8' }).trim()
}

function validateSuite(config: SuiteConfig): string[] {
  return [...validateConfig(config), ...validatePlan(config)]
}

function writeEvidence(runtime: ApplicationRuntime, root: string, name: string, value: unknown): void {
  mkdirSync(dirname(resolve(root, name)), { recursive: true })
  runtime.writeFile(resolve(root, name), `${JSON.stringify(value, null, 2)}\n`)
}

export async function runCommandSequence(
  commands: readonly CommandConfig[],
  runtime: Pick<ApplicationRuntime, 'now' | 'runPhase'>,
  artifactRoot: string,
  deadlines: SuiteDeadlines,
  abortSignal: AbortSignal,
  namePrefix = 'build',
): Promise<{ readonly passed: boolean; readonly results: readonly PhaseResult[] }> {
  const results: PhaseResult[] = []
  for (const [index, command] of commands.entries()) {
    if (abortSignal.aborted) return { passed: false, results }
    const result = await runtime.runPhase(
      `${namePrefix}-${index + 1}`,
      command.command,
      command.args,
      artifactRoot,
      deadlines,
      runtime.now,
      abortSignal,
    )
    results.push(result)
    if (!phaseSucceeded(result)) return { passed: false, results }
  }
  return { passed: true, results }
}

export async function runApplicationScope(
  application: string,
  config: SuiteConfig,
  runtime: ApplicationRuntime,
  context: ApplicationExecutionContext,
): Promise<number> {
  if (!(await prepareApplicationScope(application, config, runtime, context))) return 1
  return await runPreparedApplicationScope(application, runtime, context)
}

export async function main(
  argv: readonly string[] = process.argv.slice(2),
  runtime: ApplicationRuntime = nativeRuntime,
): Promise<number> {
  let args: ApplicationArgs
  try {
    args = parseArgs(argv)
  } catch (error) {
    process.stderr.write(`${(error as Error).message}\nusage: npm run test:app -- <application>\n`)
    return 2
  }

  let config: SuiteConfig
  try {
    config = nativeSourceConfig()
  } catch (error) {
    process.stderr.write(`could not read test plan: ${(error as Error).message}\n`)
    return 2
  }
  const configErrors = validateSuite(config)
  if (configErrors.length > 0) {
    process.stderr.write(`invalid test plan:\n${configErrors.map((error) => `  - ${error}`).join('\n')}\n`)
    return 2
  }
  if (args.help) {
    runtime.report(`usage: npm run test:app -- <application>\n${formatApplicationHelp(config)}`)
    return 0
  }
  if (args.application === undefined) {
    process.stderr.write(`usage: npm run test:app -- <application>\n${formatApplicationHelp(config)}\n`)
    return 2
  }

  let commands: readonly CommandConfig[]
  try {
    commands = applicationBuilds(config, args.application)
  } catch (error) {
    process.stderr.write(`${(error as Error).message}\n`)
    return 2
  }

  const startedAt = runtime.now()
  const runId = `${startedAt}-${runtime.pid()}`
  let artifactRoot: string
  try {
    artifactRoot = runtime.createArtifactRoot(runId)
  } catch (error) {
    process.stderr.write(`could not create application diagnostics: ${(error as Error).message}\n`)
    return 1
  }
  const suiteDeadline = config.suiteDeadlineMs
  const graceMs = config.killGraceMs ?? 5000
  const deadlines = suiteDeadlines(startedAt, suiteDeadline, graceMs)
  let sourceRevision: string
  try {
    sourceRevision = nativeSourceRevision()
  } catch (error) {
    process.stderr.write(`could not read source revision: ${(error as Error).message}\n`)
    return 1
  }
  const termination = createTerminationSignal()
  try {
    return await runApplicationScope(args.application, config, runtime, {
      runId,
      startedAt,
      deadlines,
      artifactRoot,
      abortSignal: termination.signal,
      sourceRevision,
      planIdentity: planIdentity(config),
    })
  } finally {
    termination.dispose()
  }
}

const nativeRuntime: ApplicationRuntime = {
  now: nativeTimeSource.now,
  pid: () => process.pid,
  createArtifactRoot,
  writeFile: (path, content) => writeFileSync(path, content),
  runPhase,
  runGuard,
  report: (line) => console.log(line),
}

const isMain = process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  void main().then(
    (code) => process.exit(code),
    (error) => {
      console.error(`test:app fatal error: ${(error as Error).message}`)
      process.exit(1)
    },
  )
}
