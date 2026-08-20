import { execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { createArtifactRoot, runPhase, type PhaseResult } from './canonical.js'
import {
  createTerminationSignal,
  prepareApplicationScope,
  runPreparedApplicationScope,
  type ApplicationRuntime,
  type ApplicationExecutionContext,
} from './application.js'
import { parseSuiteConfig, validateConfig } from './config.js'
import { suiteDeadlines, type SuiteDeadlines } from './deadline.js'
import { main as runGuard, type GuardRuntime } from './guard.js'
import { validateEvidence } from './gate.js'
import { planIdentity, validatePlan } from './plan.js'
import { runRepositoryScope, type RepositoryRuntime, type RepositoryExecutionContext } from './repository.js'
import { nativeTimeSource } from './time.js'
import type { SuiteConfig } from './types.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

export interface VerifyArgs {
  readonly artifactParent?: string
  readonly help: boolean
}

export function parseArgs(argv: readonly string[]): VerifyArgs {
  let artifactParent: string | undefined
  let help = false
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index]
    if (arg === '--help' || arg === '-h') help = true
    else if (arg === '--artifact-root') artifactParent = argv[++index] ?? ''
    else if (arg.startsWith('--artifact-root=')) artifactParent = arg.slice('--artifact-root='.length)
    else throw new Error(`unknown verify argument: ${arg}`)
  }
  if (artifactParent === '') throw new Error('--artifact-root must not be empty')
  return { artifactParent, help }
}

export interface VerifyRuntime {
  readonly now: () => number
  readonly pid: () => number
  readonly sourceIdentity: () => { readonly revision: string; readonly changes: string }
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
  ) => Promise<PhaseResult>
  readonly runGuard: (argv: readonly string[], runtime: GuardRuntime) => Promise<number>
  readonly report: (line: string) => void
}

function nativeSourceConfig(): SuiteConfig {
  return parseSuiteConfig(readFileSync(resolve(repoRoot, 'test-duration.config.jsonc'), 'utf8'))
}

function nativeSourceIdentity(): { readonly revision: string; readonly changes: string } {
  return {
    revision: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repoRoot, encoding: 'utf8' }).trim(),
    changes: execFileSync('git', ['status', '--porcelain=v1', '--untracked-files=all'], {
      cwd: repoRoot,
      encoding: 'utf8',
    }).trim(),
  }
}

function writeEvidence(runtime: Pick<VerifyRuntime, 'writeFile'>, root: string, name: string, value: unknown): void {
  runtime.writeFile(resolve(root, name), `${JSON.stringify(value, null, 2)}\n`)
}

function scopeRuntime(runtime: VerifyRuntime): {
  readonly application: ApplicationRuntime
  readonly repository: RepositoryRuntime
} {
  const runPhaseForScope = (
    name: string,
    command: string,
    args: readonly string[],
    artifactRoot: string,
    deadlines: SuiteDeadlines,
    now: () => number,
    abortSignal: AbortSignal,
  ) => runtime.runPhase(name, command, args, artifactRoot, deadlines, now, abortSignal)
  return {
    application: {
      now: runtime.now,
      pid: runtime.pid,
      createArtifactRoot: () => {
        throw new Error('verify scopes receive their artifact root from the parent gate')
      },
      writeFile: runtime.writeFile,
      runPhase: runPhaseForScope,
      runGuard: runtime.runGuard,
      report: runtime.report,
    },
    repository: {
      now: runtime.now,
      pid: runtime.pid,
      createArtifactRoot: () => {
        throw new Error('verify scopes receive their artifact root from the parent gate')
      },
      writeFile: runtime.writeFile,
      runPhase: runPhaseForScope,
      runGuard: runtime.runGuard,
      report: runtime.report,
    },
  }
}

export async function runVerify(
  config: SuiteConfig,
  runtime: VerifyRuntime,
  artifactRoot: string,
  startedAt: number,
  deadlines: SuiteDeadlines,
  abortSignal: AbortSignal,
  sourceRevision: string,
): Promise<number> {
  const runtimes = scopeRuntime(runtime)
  const identity = planIdentity(config)
  const applicationContexts: readonly ApplicationExecutionContext[] = config.plan!.applications.map((application) => ({
    runId: `${startedAt}-${runtime.pid()}-${application}`,
    startedAt,
    deadlines,
    artifactRoot: resolve(artifactRoot, application),
    abortSignal,
    sourceRevision,
    planIdentity: identity,
  }))

  // Server and CLI share project-reference outputs. Build every application
  // before admitting any test process, then let the isolated test scopes fan
  // out together. CI already gives each application its own runner; local
  // verify must provide the equivalent output isolation on one host.
  const builtApplications = new Map<string, boolean>()
  for (const [index, application] of config.plan!.applications.entries()) {
    const context = applicationContexts[index]
    builtApplications.set(
      application,
      await prepareApplicationScope(application, config, runtimes.application, context),
    )
  }

  const scopeResults = await Promise.all([
    ...applicationContexts.map((context, index) => {
      const application = config.plan!.applications[index]
      return builtApplications.get(application)
        ? runPreparedApplicationScope(application, runtimes.application, context)
        : Promise.resolve(1)
    }),
    (() => {
      const context: RepositoryExecutionContext = {
        runId: `${startedAt}-${runtime.pid()}-${config.plan!.repositoryScope}`,
        startedAt,
        deadlines,
        artifactRoot: resolve(artifactRoot, config.plan!.repositoryScope),
        abortSignal,
        sourceRevision,
        planIdentity: identity,
      }
      return runRepositoryScope(config, runtimes.repository, context)
    })(),
  ])

  const evidenceErrors = validateEvidence(config, artifactRoot)
  const passed =
    scopeResults.every((code) => code === 0) &&
    evidenceErrors.length === 0 &&
    !abortSignal.aborted &&
    runtime.now() < deadlines.hardDeadlineAt
  writeEvidence(runtime, artifactRoot, 'summary.json', {
    schemaVersion: 1,
    sourceRevision,
    suiteStart: startedAt,
    hardDeadlineAt: deadlines.hardDeadlineAt,
    passed,
    scopeResults,
    evidenceErrors,
  })
  if (!passed) {
    for (const error of evidenceErrors) process.stderr.write(`verify: ${error}\n`)
  }
  return passed ? 0 : 1
}

const nativeRuntime: VerifyRuntime = {
  now: nativeTimeSource.now,
  pid: () => process.pid,
  sourceIdentity: nativeSourceIdentity,
  createArtifactRoot,
  writeFile: (path, content) => writeFileSync(path, content),
  runPhase,
  runGuard,
  report: (line) => console.log(line),
}

export async function main(
  argv: readonly string[] = process.argv.slice(2),
  runtime: VerifyRuntime = nativeRuntime,
): Promise<number> {
  let args: VerifyArgs
  try {
    args = parseArgs(argv)
  } catch (error) {
    process.stderr.write(`${(error as Error).message}\nusage: npm run verify [--artifact-root <external-dir>]\n`)
    return 2
  }
  if (args.help) {
    runtime.report('usage: npm run verify [--artifact-root <external-dir>]')
    return 0
  }

  let config: SuiteConfig
  try {
    config = nativeSourceConfig()
  } catch (error) {
    process.stderr.write(`could not read test plan: ${(error as Error).message}\n`)
    return 2
  }
  const errors = [...validateConfig(config), ...validatePlan(config)]
  if (errors.length > 0) {
    process.stderr.write(`invalid test plan:\n${errors.map((error) => `  - ${error}`).join('\n')}\n`)
    return 2
  }

  let source: { readonly revision: string; readonly changes: string }
  try {
    source = runtime.sourceIdentity()
  } catch (error) {
    process.stderr.write(`could not read source identity: ${(error as Error).message}\n`)
    return 1
  }
  if (source.changes) {
    process.stderr.write('verify requires a clean index and worktree\n')
    return 1
  }

  const startedAt = runtime.now()
  const runId = `${startedAt}-${runtime.pid()}`
  let artifactRoot: string
  try {
    artifactRoot = runtime.createArtifactRoot(runId, args.artifactParent)
  } catch (error) {
    process.stderr.write(`could not create verify diagnostics: ${(error as Error).message}\n`)
    return 1
  }
  const graceMs = config.killGraceMs ?? 5000
  const deadlines = suiteDeadlines(startedAt, config.suiteDeadlineMs, graceMs)
  const termination = createTerminationSignal()
  try {
    runtime.report(`verify diagnostics: ${artifactRoot}`)
    writeEvidence(runtime, artifactRoot, 'run.json', {
      runId,
      startedAt,
      suiteDeadlineMs: config.suiteDeadlineMs,
      sourceRevision: source.revision,
    })
    return await runVerify(config, runtime, artifactRoot, startedAt, deadlines, termination.signal, source.revision)
  } catch (error) {
    writeEvidence(runtime, artifactRoot, 'fatal-error.json', {
      message: error instanceof Error ? error.message : String(error),
    })
    process.stderr.write(`verify failed: ${(error as Error).message}\n`)
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
      console.error(`verify fatal error: ${(error as Error).message}`)
      process.exit(1)
    },
  )
}
