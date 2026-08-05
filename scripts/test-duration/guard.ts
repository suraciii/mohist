import { execFileSync, spawn } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { dirname, resolve, basename } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { evaluateTrack } from './budget.js'
import { parseSuiteConfig, validateConfig } from './config.js'
import { formatEvaluation, formatSummary, formatTrackRun, summarize } from './diagnostics.js'
import { runWithDeadline } from './deadline.js'
import { parseReport } from './reports.js'
import { parseAssemblyName, resolveApphostPath, resolveFocusedCommand } from './focused.js'
import type { SuiteConfig, TrackConfig, TrackEvaluation, TrackRun } from './types.js'

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

function killTree(pid: number, graceMs: number): Promise<void> {
  return new Promise((done) => {
    const signal = (sig: NodeJS.Signals) => {
      try {
        if (process.platform === 'win32') {
          execFileSync('taskkill', ['/pid', String(pid), '/T', '/F'], { stdio: 'ignore' })
        } else {
          process.kill(-pid, sig)
        }
      } catch {
        // already gone or not permitted
      }
    }
    signal('SIGTERM')
    setTimeout(() => {
      signal('SIGKILL')
      done()
    }, graceMs).unref()
  })
}

interface SpawnedChild {
  readonly done: Promise<{ exitCode: number | null }>
  readonly pid: number
}

function spawnChild(command: string, args: readonly string[]): SpawnedChild {
  const detached = process.platform !== 'win32'
  const child = spawn(command, args as string[], {
    cwd: repoRoot,
    stdio: 'inherit',
    detached,
  })
  const done = new Promise<{ exitCode: number | null }>((resolvePromise) => {
    child.on('exit', (code) => resolvePromise({ exitCode: code }))
    child.on('error', () => resolvePromise({ exitCode: 1 }))
  })
  return { done, pid: child.pid ?? -1 }
}

function runTrack(track: TrackConfig, graceMs: number, suiteDeadline: Promise<void>): Promise<TrackRun> {
  const { command, args } = commandFor(track)
  const cmdString = `${command} ${args.join(' ')}`
  const child = spawnChild(command, args)
  const trackDeadline = new Promise<void>((fire) => setTimeout(() => fire(), track.deadlineMs).unref())
  const outcome = runWithDeadline({
    start: () => child.done,
    kill: () => killTree(child.pid, graceMs),
    timeout: Promise.race([
      trackDeadline.then(() => 'track' as const),
      suiteDeadline.then(() => 'suite' as const),
    ]),
    now: () => Date.now(),
  })
  return outcome.then((result) => ({
    trackId: track.id,
    timedOut: result.status === 'timeout',
    timeoutReason: result.timeoutReason,
    exitCode: result.exitCode,
    elapsedMs: result.elapsedMs,
    deadlineMs: track.deadlineMs,
    command: cmdString,
  }))
}

function evaluateFromFile(track: TrackConfig): TrackEvaluation {
  const content = readFileSync(resolve(repoRoot, track.report), 'utf8')
  const cases = parseReport(track.reportFormat, content)
  return evaluateTrack(track, cases, new Date())
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
    const suiteTimer = new Promise<void>((fire) => {
      setTimeout(() => {
        suiteExpired = true
        fire()
      }, config.suiteDeadlineMs).unref()
    })
    for (const track of selected) {
      if (suiteExpired) break
      const run = await runTrack(track, graceMs, suiteTimer)
      runs.push(run)
      if (run.timeoutReason === 'track') console.error(`  ${track.id}: exceeded ${track.deadlineMs}ms deadline`)
      if (suiteExpired) break
    }
    const suiteElapsed = Date.now() - suiteStart
    if (suiteExpired || suiteElapsed >= config.suiteDeadlineMs) {
      console.error(`suite deadline breached after ${suiteElapsed}ms`)
      return 1
    }
  }

  for (const track of selected) {
    try {
      evaluations.push(evaluateFromFile(track))
    } catch (error) {
      process.stderr.write(`  ${track.id}: could not read report ${track.report}: ${(error as Error).message}\n`)
      evaluations.push({
        trackId: track.id,
        enforce: track.enforce,
        total: 0,
        failedTests: [],
        rules: [],
        passed: false,
      })
    }
  }

  if (mode === 'run') {
    console.log('runs:')
    for (const run of runs) console.log(formatTrackRun(run))
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
  void main().then((code) => process.exit(code))
}
