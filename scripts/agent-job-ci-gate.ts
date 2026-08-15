import { createWriteStream, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { spawn, type ChildProcess } from 'node:child_process'
import { dirname, basename, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { parseAssemblyName, resolveFocusedCommand } from './test-duration/focused.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const csproj = 'packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj'
const timeoutArgs = ['--signal=TERM', '--kill-after=30s', '180s'] as const

export const gateRounds = 10

export const targets = [
  {
    id: 'observer',
    className: 'Mohist.Server.SpecTests.Specs.Agent.Grain.AgentJobDispatchObserverSpecs',
  },
  {
    id: 'grain',
    className: 'Mohist.Server.SpecTests.Specs.Agent.Grain.AgentJobGrainSpecs',
  },
] as const

export interface XunitSummary {
  readonly total: number
  readonly passed: number
  readonly failed: number
  readonly errors: number
  readonly skipped: number
  readonly notRun: number
  readonly other: number
}

export interface CommandResult {
  readonly exitCode: number | null
  readonly signal: NodeJS.Signals | null
  readonly elapsedMs: number
  readonly reportPath: string
  readonly stdoutPath: string
  readonly stderrPath: string
  readonly spawnError?: string
}

export interface GateRecord {
  readonly target: string
  readonly className: string
  readonly mode: 'single' | 'parallel'
  readonly round: number
  readonly ok: boolean
  readonly exitCode: number | null
  readonly signal: NodeJS.Signals | null
  readonly elapsedMs: number
  readonly reportPath: string
  readonly stdoutPath: string
  readonly stderrPath: string
  readonly summary?: XunitSummary
  readonly reasons: readonly string[]
}

export interface GateCommand {
  readonly target: string
  readonly className: string
  readonly mode: 'single' | 'parallel'
  readonly round: number
  readonly reportPath: string
  readonly stdoutPath: string
  readonly stderrPath: string
}

export interface RunningCommand<T = CommandResult> {
  readonly result: Promise<T>
  readonly kill: () => void
}

export type CommandExecutor = (command: GateCommand, apphost: string, args: readonly string[]) => RunningCommand

function xmlAttribute(block: string, name: string): string | undefined {
  const match = block.match(new RegExp(`\\b${name}="([^"]*)"`))
  return match?.[1]
}

export function parseTrxSummary(xml: string): XunitSummary {
  if (!/<TestRun(?:\s|>)/.test(xml)) throw new Error('TRX report has no TestRun root')

  let total = 0
  let passed = 0
  let failed = 0
  let errors = 0
  let skipped = 0
  let notRun = 0
  let other = 0
  const resultPattern = /<UnitTestResult\b([^>]*)>/g
  for (const match of xml.matchAll(resultPattern)) {
    const outcome = xmlAttribute(match[1], 'outcome')
    total++
    if (outcome === 'Passed') passed++
    else if (outcome === 'Failed') failed++
    else if (outcome === 'Error') errors++
    else if (outcome === 'NotExecuted' || outcome === 'Skipped') skipped++
    else if (outcome === 'NotRun') notRun++
    else other++
  }
  return { total, passed, failed, errors, skipped, notRun, other }
}

export function parseXunitSummary(output: string): XunitSummary {
  const match = output.match(
    /Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Not Run:\s*(\d+)/,
  )
  if (!match) throw new Error('xUnit execution summary is missing')
  const total = Number(match[1])
  const errors = Number(match[2])
  const failed = Number(match[3])
  const skipped = Number(match[4])
  const notRun = Number(match[5])
  const passed = total - errors - failed - skipped - notRun
  return { total, passed: Math.max(0, passed), failed, errors, skipped, notRun, other: passed < 0 ? 1 : 0 }
}

export function validateSummary(summary: XunitSummary): readonly string[] {
  const reasons: string[] = []
  if (summary.total === 0) reasons.push('Total: 0')
  if (summary.failed !== 0) reasons.push(`Failed: ${summary.failed}`)
  if (summary.errors !== 0) reasons.push(`Errors: ${summary.errors}`)
  if (summary.skipped !== 0) reasons.push(`Skipped: ${summary.skipped}`)
  if (summary.notRun !== 0) reasons.push(`Not Run: ${summary.notRun}`)
  if (summary.other !== 0) reasons.push(`unknown outcomes: ${summary.other}`)
  if (summary.passed !== summary.total) reasons.push(`Passed: ${summary.passed}/${summary.total}`)
  return reasons
}

export function validateResult(result: CommandResult): {
  readonly ok: boolean
  readonly summary?: XunitSummary
  readonly reasons: readonly string[]
} {
  const reasons: string[] = []
  if (result.spawnError) reasons.push(`spawn error: ${result.spawnError}`)
  if (result.exitCode !== 0) reasons.push(`exit code: ${result.exitCode ?? 'null'}`)
  if (result.signal) reasons.push(`signal: ${result.signal}`)

  let summary: XunitSummary | undefined
  try {
    if (!existsSync(result.reportPath) || !statSync(result.reportPath).isFile()) {
      reasons.push(`missing report: ${result.reportPath}`)
    } else {
      const reportSummary = parseTrxSummary(readFileSync(result.reportPath, 'utf8'))
      try {
        const output = `${readFileSync(result.stdoutPath, 'utf8')}\n${readFileSync(result.stderrPath, 'utf8')}`
        summary = parseXunitSummary(output)
        for (const field of ['total', 'failed', 'errors', 'skipped', 'notRun'] as const) {
          if (summary[field] !== reportSummary[field]) {
            reasons.push(`summary mismatch for ${field}: output=${summary[field]} report=${reportSummary[field]}`)
          }
        }
        reasons.push(...validateSummary(summary))
        reasons.push(...validateSummary(reportSummary))
      } catch (error) {
        reasons.push(`summary parse error: ${(error as Error).message}`)
        reasons.push(...validateSummary(reportSummary))
      }
    }
  } catch (error) {
    reasons.push(`report parse error: ${(error as Error).message}`)
  }

  return { ok: reasons.length === 0, summary, reasons }
}

function recordFor(command: GateCommand, result: CommandResult): GateRecord {
  const validation = validateResult(result)
  return {
    target: command.target,
    className: command.className,
    mode: command.mode,
    round: command.round,
    ok: validation.ok,
    exitCode: result.exitCode,
    signal: result.signal,
    elapsedMs: result.elapsedMs,
    reportPath: command.reportPath,
    stdoutPath: command.stdoutPath,
    stderrPath: command.stderrPath,
    summary: validation.summary,
    reasons: validation.reasons,
  }
}

function formatRecord(record: GateRecord): string {
  const summary = record.summary
  const counts = summary
    ? `Total=${summary.total} Passed=${summary.passed} Failed=${summary.failed} Errors=${summary.errors} Skipped=${summary.skipped} NotRun=${summary.notRun}`
    : 'Total=unknown Passed=unknown Failed=unknown Errors=unknown Skipped=unknown NotRun=unknown'
  const status = record.ok ? 'PASS' : 'FAIL'
  const reason = record.reasons.length > 0 ? ` reasons=${record.reasons.join('; ')}` : ''
  return `${status} mode=${record.mode} target=${record.target} round=${record.round} exit=${record.exitCode ?? 'null'} wallMs=${record.elapsedMs} ${counts}${reason}`
}

function writeEvidence(outputDir: string, records: readonly GateRecord[], failure?: string): void {
  mkdirSync(outputDir, { recursive: true })
  writeFileSync(
    resolve(outputDir, 'gate-summary.json'),
    JSON.stringify({ rounds: gateRounds, records, failure }, null, 2) + '\n',
  )
  const lines = records.map(formatRecord)
  if (failure) lines.push(`GATE FAIL: ${failure}`)
  else if (records.length > 0) lines.push(`GATE PASS: ${records.length} invocations`)
  writeFileSync(resolve(outputDir, 'gate-summary.txt'), lines.join('\n') + '\n')
}

function killProcess(child: ChildProcess): void {
  if (child.pid && child.pid > 1 && process.platform !== 'win32') {
    try {
      process.kill(-child.pid, 'SIGTERM')
      return
    } catch {
      // The process may already have exited.
    }
  }
  child.kill('SIGTERM')
}

export const spawnExecutor: CommandExecutor = (command, apphost, focusedArgs) => {
  mkdirSync(dirname(command.reportPath), { recursive: true })
  mkdirSync(dirname(command.stdoutPath), { recursive: true })
  try {
    rmSync(command.reportPath, { force: true })
    rmSync(command.stdoutPath, { force: true })
    rmSync(command.stderrPath, { force: true })
  } catch {
    // The following process/report checks turn an inaccessible artifact into a gate failure.
  }

  const stdout = createWriteStream(command.stdoutPath)
  const stderr = createWriteStream(command.stderrPath)
  const started = Date.now()
  const child = spawn('timeout', [...timeoutArgs, apphost, ...focusedArgs, '-trx', command.reportPath], {
    cwd: repoRoot,
    detached: process.platform !== 'win32',
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  child.stdout?.pipe(stdout)
  child.stderr?.pipe(stderr)

  const result = new Promise<CommandResult>((resolveResult) => {
    let spawnError: string | undefined
    child.once('error', (error) => {
      spawnError = error.message
    })
    child.once('close', (exitCode, signal) => {
      stdout.end()
      stderr.end()
      resolveResult({
        exitCode,
        signal,
        elapsedMs: Date.now() - started,
        reportPath: command.reportPath,
        stdoutPath: command.stdoutPath,
        stderrPath: command.stderrPath,
        spawnError,
      })
    })
  })

  return { result, kill: () => killProcess(child) }
}

function apphostFor(className: string): { apphost: string; args: readonly string[] } {
  const projectPath = resolve(repoRoot, csproj)
  const xml = readFileSync(projectPath, 'utf8')
  const assemblyName = parseAssemblyName(xml) ?? basename(projectPath).replace(/\.csproj$/, '')
  return resolveFocusedCommand({
    csprojXml: xml,
    className,
    projectDir: dirname(projectPath),
    assemblyName,
  })
}

function commandFor(
  outputDir: string,
  target: (typeof targets)[number],
  mode: GateCommand['mode'],
  round: number,
): GateCommand {
  const prefix =
    mode === 'single' ? `single-${String(round).padStart(2, '0')}` : `parallel-${String(round).padStart(2, '0')}`
  const base = resolve(outputDir, prefix, target.id)
  return {
    target: target.id,
    className: target.className,
    mode,
    round,
    reportPath: resolve(base, 'results.trx'),
    stdoutPath: resolve(base, 'stdout.log'),
    stderrPath: resolve(base, 'stderr.log'),
  }
}

async function runSingle(
  outputDir: string,
  target: (typeof targets)[number],
  round: number,
  executor: CommandExecutor,
): Promise<GateRecord> {
  const command = commandFor(outputDir, target, 'single', round)
  const focused = apphostFor(target.className)
  return recordFor(command, await executor(command, focused.apphost, focused.args).result)
}

export async function runParallelFailFast<T, V, R>(
  commands: readonly T[],
  executor: (command: T) => RunningCommand<V>,
  isSuccess: (result: R) => boolean,
  readResult: (command: T, result: V) => R,
): Promise<readonly R[]> {
  const running = commands.map((command) => executor(command))
  const first = await Promise.race(
    running.map((process, index) => process.result.then((result) => ({ index, result }))),
  )
  const firstResult = readResult(commands[first.index], first.result)
  if (!isSuccess(firstResult)) {
    running.forEach((process, index) => {
      if (index !== first.index) process.kill()
    })
  }
  const results = await Promise.all(running.map((process) => process.result))
  return results.map((result, index) => readResult(commands[index], result))
}

async function runParallel(outputDir: string, round: number, executor: CommandExecutor): Promise<GateRecord[]> {
  const commands = targets.map((target) => ({ command: commandFor(outputDir, target, 'parallel', round), target }))
  return [
    ...(await runParallelFailFast<(typeof commands)[number], CommandResult, GateRecord>(
      commands,
      (command) => {
        const focused = apphostFor(command.target.className)
        return executor(command.command, focused.apphost, focused.args)
      },
      (record) => record.ok,
      (command, result) => recordFor(command.command, result as CommandResult),
    )),
  ]
}

export interface GateOptions {
  readonly outputDir?: string
  readonly rounds?: number
  readonly executor?: CommandExecutor
}

export async function runGate(options: GateOptions = {}): Promise<number> {
  const outputDir = resolve(repoRoot, options.outputDir ?? 'artifacts/agent-job-ci-gate')
  const rounds = options.rounds ?? gateRounds
  const executor = options.executor ?? spawnExecutor
  rmSync(outputDir, { recursive: true, force: true })
  mkdirSync(outputDir, { recursive: true })
  const records: GateRecord[] = []
  let failure: string | undefined

  for (const target of targets) {
    for (let round = 1; round <= rounds; round++) {
      const record = await runSingle(outputDir, target, round, executor)
      records.push(record)
      console.log(formatRecord(record))
      if (!record.ok) {
        failure = `${record.mode} ${record.target} round ${record.round}: ${record.reasons.join('; ')}`
        writeEvidence(outputDir, records, failure)
        return 1
      }
      writeEvidence(outputDir, records)
    }
  }

  for (let round = 1; round <= rounds; round++) {
    const roundRecords = await runParallel(outputDir, round, executor)
    for (const record of roundRecords) {
      records.push(record)
      console.log(formatRecord(record))
    }
    const failed = roundRecords.find((record) => !record.ok)
    if (failed) {
      failure = `${failed.mode} ${failed.target} round ${failed.round}: ${failed.reasons.join('; ')}`
      writeEvidence(outputDir, records, failure)
      return 1
    }
    writeEvidence(outputDir, records)
  }

  writeEvidence(outputDir, records)
  return 0
}

const isMain = process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  void runGate().then(
    (code) => process.exit(code),
    (error) => {
      console.error(`agent-job-ci-gate: fatal: ${(error as Error).message}`)
      process.exit(1)
    },
  )
}
