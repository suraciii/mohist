import { execFileSync } from 'node:child_process'
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { dirname, isAbsolute, join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { parseSuiteConfig, validateConfig } from './config.js'
import { planIdentity, selectApplicationTracks, selectRepositoryTracks, validatePlan } from './plan.js'
import type { CommandConfig, SuiteConfig } from './types.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

export interface GateArgs {
  readonly evidenceRoot?: string
  readonly help: boolean
}

export function parseArgs(argv: readonly string[]): GateArgs {
  let evidenceRoot: string | undefined
  let help = false
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index]
    if (arg === '--help' || arg === '-h') help = true
    else if (arg === '--evidence-root') evidenceRoot = argv[++index] ?? ''
    else if (arg.startsWith('--evidence-root=')) evidenceRoot = arg.slice('--evidence-root='.length)
    else throw new Error(`unknown gate argument: ${arg}`)
  }
  return { evidenceRoot, help }
}

function readJson(path: string): unknown {
  try {
    return JSON.parse(readFileSync(path, 'utf8')) as unknown
  } catch {
    return undefined
  }
}

function object(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined
}

function validateTrackEvidence(scope: string, expectedTrackIds: readonly string[], root: string): string[] {
  const errors: string[] = []
  const summaryPath = join(root, 'summary.json')
  const summary = object(readJson(summaryPath))
  if (summary === undefined) {
    errors.push(`${scope}: missing or invalid summary.json`)
    return errors
  }
  if (summary.passed !== true) errors.push(`${scope}: summary.passed is not true`)
  const evaluations = Array.isArray(summary.evaluations) ? summary.evaluations : undefined
  if (evaluations === undefined) {
    errors.push(`${scope}: summary.evaluations must be an array`)
    return errors
  }
  const seenEvaluations = new Set<string>()
  const expectedSet = new Set(expectedTrackIds)
  for (const value of evaluations) {
    const evaluation = object(value)
    const trackId = evaluation?.trackId
    if (typeof trackId !== 'string') {
      errors.push(`${scope}: evaluation is missing trackId`)
      continue
    }
    if (seenEvaluations.has(trackId)) errors.push(`${scope}: duplicate evaluation for ${trackId}`)
    if (!expectedSet.has(trackId)) errors.push(`${scope}: unexpected evaluation for ${trackId}`)
    seenEvaluations.add(trackId)
    if (typeof evaluation.total !== 'number' || evaluation.total <= 0) {
      errors.push(`${scope}: ${trackId} has no executed cases`)
    }
    if (evaluation.passed !== true) errors.push(`${scope}: ${trackId} did not pass evaluation`)
  }
  for (const trackId of expectedTrackIds) {
    if (!seenEvaluations.has(trackId)) errors.push(`${scope}: missing evaluation for ${trackId}`)
  }

  const runs = Array.isArray(summary.runs) ? summary.runs : undefined
  if (runs === undefined) {
    errors.push(`${scope}: summary.runs must be an array`)
    return errors
  }
  const seenRuns = new Set<string>()
  for (const value of runs) {
    const run = object(value)
    const trackId = run?.trackId
    if (typeof trackId !== 'string') {
      errors.push(`${scope}: run is missing trackId`)
      continue
    }
    if (seenRuns.has(trackId)) errors.push(`${scope}: duplicate run for ${trackId}`)
    if (!expectedSet.has(trackId)) errors.push(`${scope}: unexpected run for ${trackId}`)
    seenRuns.add(trackId)
    if (run.exitCode !== 0 || run.reportReady !== true || run.cleanupComplete !== true) {
      errors.push(`${scope}: ${trackId} did not produce clean passing evidence`)
    }
    if (run.cancelled === true || run.timedOut === true) errors.push(`${scope}: ${trackId} was cancelled or timed out`)
  }
  for (const trackId of expectedTrackIds) {
    if (!seenRuns.has(trackId)) errors.push(`${scope}: missing run for ${trackId}`)
  }
  return errors
}

function validateRepositoryEvidence(
  scope: string,
  expectedChecks: readonly CommandConfig[],
  expectedTrackIds: readonly string[],
  root: string,
): string[] {
  const errors: string[] = []
  const summary = object(readJson(join(root, 'summary.json')))
  if (summary === undefined) {
    errors.push(`${scope}: missing or invalid summary.json`)
    return errors
  }
  if (summary.passed !== true) errors.push(`${scope}: summary.passed is not true`)
  const checksJson = readJson(join(root, 'checks.json'))
  const checks = Array.isArray(checksJson) ? checksJson : Array.isArray(summary.checks) ? summary.checks : undefined
  if (checks === undefined || checks.length === 0) {
    errors.push(`${scope}: repository checks are missing`)
  } else {
    if (checks.length !== expectedChecks.length) {
      errors.push(`${scope}: expected ${expectedChecks.length} repository checks, received ${checks.length}`)
    }
    checks.forEach((value, index) => {
      const check = object(value)
      const expected = expectedChecks[index]
      if (
        expected === undefined ||
        check?.command !== expected.command ||
        !Array.isArray(check.args) ||
        check.args.length !== expected.args.length ||
        check.args.some((arg, argIndex) => arg !== expected.args[argIndex])
      ) {
        errors.push(`${scope}: check ${index + 1} does not match the declared repository plan`)
      }
      if (check?.exitCode !== 0 || check.timedOut === true || check.cleanupComplete !== true) {
        errors.push(`${scope}: check ${index + 1} did not pass cleanly`)
      }
    })
  }
  if (expectedTrackIds.length > 0) errors.push(...validateTrackEvidence(scope, expectedTrackIds, root))
  return errors
}

export function validateEvidence(config: SuiteConfig, evidenceRoot: string, expectedSourceRevision?: string): string[] {
  const errors: string[] = []
  if (!isAbsolute(evidenceRoot)) return ['--evidence-root must be an absolute path']
  if (!existsSync(evidenceRoot)) return [`evidence root does not exist: ${evidenceRoot}`]
  const applications = config.plan!.applications
  const repositoryScope = config.plan!.repositoryScope
  const expectedPlanIdentity = planIdentity(config)
  const sourceRevisions = new Set<string>()
  const expectedScopes = [...applications, repositoryScope]
  const expectedSet = new Set(expectedScopes)
  for (const entry of readdirSync(evidenceRoot, { withFileTypes: true })) {
    if (entry.isDirectory() && !expectedSet.has(entry.name)) errors.push(`unexpected evidence scope: ${entry.name}`)
  }
  for (const application of applications) {
    const root = join(evidenceRoot, application)
    const metadata = object(readJson(join(root, 'application.json')))
    if (metadata?.application !== application)
      errors.push(`${application}: application metadata is missing or mismatched`)
    if (metadata?.planIdentity !== expectedPlanIdentity)
      errors.push(`${application}: plan identity is missing or mismatched`)
    if (typeof metadata?.sourceRevision !== 'string' || metadata.sourceRevision.length === 0) {
      errors.push(`${application}: source revision is missing or invalid`)
    } else {
      sourceRevisions.add(metadata.sourceRevision)
    }
    const tracks = selectApplicationTracks(config, application).tracks.map((track) => track.id)
    errors.push(...validateTrackEvidence(application, tracks, root))
  }
  const repositoryRoot = join(evidenceRoot, repositoryScope)
  const metadata = object(readJson(join(repositoryRoot, 'repository.json')))
  if (metadata?.scope !== repositoryScope)
    errors.push(`${repositoryScope}: repository metadata is missing or mismatched`)
  if (metadata?.planIdentity !== expectedPlanIdentity)
    errors.push(`${repositoryScope}: plan identity is missing or mismatched`)
  if (typeof metadata?.sourceRevision !== 'string' || metadata.sourceRevision.length === 0) {
    errors.push(`${repositoryScope}: source revision is missing or invalid`)
  } else {
    sourceRevisions.add(metadata.sourceRevision)
  }
  const repositoryTracks = selectRepositoryTracks(config).tracks.map((track) => track.id)
  errors.push(
    ...validateRepositoryEvidence(repositoryScope, config.plan!.repositoryChecks, repositoryTracks, repositoryRoot),
  )
  if (sourceRevisions.size > 1) errors.push('evidence scopes have mismatched source revisions')
  if (expectedSourceRevision !== undefined && !sourceRevisions.has(expectedSourceRevision)) {
    errors.push(`evidence scopes do not match checked-out source revision: ${expectedSourceRevision}`)
  }
  return errors
}

function loadConfig(): SuiteConfig {
  return parseSuiteConfig(readFileSync(resolve(repoRoot, 'test-duration.config.jsonc'), 'utf8'))
}

export function main(argv: readonly string[] = process.argv.slice(2)): number {
  let args: GateArgs
  try {
    args = parseArgs(argv)
  } catch (error) {
    process.stderr.write(`${(error as Error).message}\n`)
    return 2
  }
  let config: SuiteConfig
  try {
    config = loadConfig()
  } catch (error) {
    process.stderr.write(`could not read test plan: ${(error as Error).message}\n`)
    return 2
  }
  const configErrors = [...validateConfig(config), ...validatePlan(config)]
  if (configErrors.length > 0) {
    process.stderr.write(`invalid test plan:\n${configErrors.map((error) => `  - ${error}`).join('\n')}\n`)
    return 2
  }
  if (args.help) {
    console.log('usage: gate --evidence-root <absolute-path>')
    return 0
  }
  if (!args.evidenceRoot) {
    process.stderr.write('usage: gate --evidence-root <absolute-path>\n')
    return 2
  }
  let sourceRevision: string
  try {
    sourceRevision = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repoRoot, encoding: 'utf8' }).trim()
  } catch (error) {
    process.stderr.write(`could not read source revision: ${(error as Error).message}\n`)
    return 1
  }
  const errors = validateEvidence(config, resolve(args.evidenceRoot), sourceRevision)
  if (errors.length > 0) {
    console.error(`Gate: FAIL (${errors.length} errors)`)
    for (const error of errors) console.error(`  - ${error}`)
    return 1
  }
  console.log(`Gate: PASS (${config.plan!.applications.length + 1} scopes)`)
  return 0
}

const isMain = process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) process.exitCode = main()
