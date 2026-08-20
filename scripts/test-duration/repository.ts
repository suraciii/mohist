import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { createArtifactRoot, runPhase } from './canonical.js'
import { parseSuiteConfig, validateConfig } from './config.js'
import { suiteDeadlines, type SuiteDeadlines } from './deadline.js'
import { main as runGuard, type GuardRuntime } from './guard.js'
import { createTerminationSignal, runCommandSequence, type ApplicationRuntime } from './application.js'
import { selectRepositoryTracks, validatePlan } from './plan.js'
import { nativeTimeSource } from './time.js'
import type { SuiteConfig } from './types.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

export interface RepositoryArgs {
  readonly help: boolean
}

export function parseArgs(argv: readonly string[]): RepositoryArgs {
  let help = false
  for (const arg of argv) {
    if (arg === '--help' || arg === '-h') help = true
    else throw new Error(`unknown repository scope argument: ${arg}`)
  }
  return { help }
}

export type RepositoryRuntime = Pick<
  ApplicationRuntime,
  'now' | 'pid' | 'createArtifactRoot' | 'writeFile' | 'runPhase' | 'report'
> & {
  readonly runGuard: (argv: readonly string[], runtime: GuardRuntime) => Promise<number>
}

function nativeSourceConfig(): SuiteConfig {
  return parseSuiteConfig(readFileSync(resolve(repoRoot, 'test-duration.config.jsonc'), 'utf8'))
}

function writeEvidence(runtime: RepositoryRuntime, root: string, name: string, value: unknown): void {
  mkdirSync(dirname(resolve(root, name)), { recursive: true })
  runtime.writeFile(resolve(root, name), `${JSON.stringify(value, null, 2)}\n`)
}

export async function main(
  argv: readonly string[] = process.argv.slice(2),
  runtime: RepositoryRuntime = nativeRuntime,
): Promise<number> {
  let args: RepositoryArgs
  try {
    args = parseArgs(argv)
  } catch (error) {
    process.stderr.write(`${(error as Error).message}\nusage: repository scope is an internal CI executor\n`)
    return 2
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
  const checks = config.plan!.repositoryChecks
  if (args.help) {
    runtime.report(
      `repository checks:\n${checks.map((check) => `  ${check.command} ${check.args.join(' ')}`).join('\n')}`,
    )
    return 0
  }

  const startedAt = runtime.now()
  const runId = `${startedAt}-${runtime.pid()}`
  let artifactRoot: string
  try {
    artifactRoot = runtime.createArtifactRoot(runId)
  } catch (error) {
    process.stderr.write(`could not create repository diagnostics: ${(error as Error).message}\n`)
    return 1
  }
  const graceMs = config.killGraceMs ?? 5000
  const deadlines = suiteDeadlines(startedAt, config.suiteDeadlineMs, graceMs)
  const termination = createTerminationSignal()
  try {
    runtime.report(`repository scope diagnostics: ${artifactRoot}`)
    writeEvidence(runtime, artifactRoot, 'run.json', {
      runId,
      startedAt,
      suiteDeadlineMs: config.suiteDeadlineMs,
    })
    writeEvidence(runtime, artifactRoot, 'repository.json', {
      scope: config.plan!.repositoryScope,
      checks,
    })

    const result = await runCommandSequence(checks, runtime, artifactRoot, deadlines, termination.signal, 'check')
    const checkEvidence = checks.map((check, index) => ({
      command: check.command,
      args: check.args,
      ...(result.results[index] ?? { exitCode: null, timedOut: false, cleanupComplete: false }),
    }))
    if (!result.passed) {
      writeEvidence(runtime, artifactRoot, 'checks.json', checkEvidence)
      writeEvidence(runtime, artifactRoot, 'summary.json', {
        schemaVersion: 1,
        scope: 'repository',
        scopeId: config.plan!.repositoryScope,
        passed: false,
        checks: checkEvidence,
      })
      return 1
    }
    writeEvidence(runtime, artifactRoot, 'checks.json', checkEvidence)

    const repositoryTracks = selectRepositoryTracks(config).tracks
    let guardCode = 0
    if (repositoryTracks.length > 0) {
      guardCode = await runtime.runGuard(
        [
          '--repository',
          '--run-root',
          artifactRoot,
          '--require-enforced',
          '--suite-deadline-at-ms',
          String(deadlines.hardDeadlineAt),
        ],
        { now: runtime.now, abortSignal: termination.signal },
      )
    }
    if (guardCode !== 0) return guardCode
    writeEvidence(runtime, artifactRoot, 'summary.json', {
      schemaVersion: 1,
      scope: 'repository',
      scopeId: config.plan!.repositoryScope,
      passed: true,
      checks: checkEvidence,
      selectedTracks: repositoryTracks.map((track) => track.id),
    })
    return 0
  } catch (error) {
    writeEvidence(runtime, artifactRoot, 'fatal-error.json', {
      message: error instanceof Error ? error.message : String(error),
    })
    process.stderr.write(`repository scope failed: ${(error as Error).message}\n`)
    return 1
  } finally {
    termination.dispose()
  }
}

const nativeRuntime: RepositoryRuntime = {
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
      console.error(`repository scope fatal error: ${(error as Error).message}`)
      process.exit(1)
    },
  )
}
