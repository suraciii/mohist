import { spawn } from "node:child_process"
import type { ChildProcess, ChildProcessWithoutNullStreams, SpawnOptions } from "node:child_process"
import { readFile } from "node:fs/promises"
import { StringDecoder } from "node:string_decoder"
import { assertExternalProcessAllowed, registerExternalProcess } from "./process-policy.js"
import { createTimeoutSignal } from "./timeout-signal.js"
import { currentRunnerFileSystem, currentRunnerResources } from "./filesystem.js"

export interface CommandResult {
  exitCode: number
  stdout: string
  stderr: string
  /**
   * Present only when the per-command timeout fired. The normal-exit path
   * constructs the result without this key so the serialized object stays
   * byte-identical to its pre-timeout shape.
   */
  status?: "timeout"
  /** True when the command was killed by a per-work containment bound. */
  resourceContainment?: true
  /**
   * The per-command timeout (ms) that fired. Absent on normal exit.
   */
  timeoutMs?: number
}

export interface CommandResourceLimits {
  /** Aggregate process-tree memory bound in MiB. */
  memoryMb?: number | null
  /** Wall-clock bound for the command in milliseconds. */
  wallClockMs?: number | null
  /** Aggregate-RSS sampling interval in milliseconds. */
  watchdogIntervalMs?: number | null
}

export type ProcessSpawner = (command: string, args: string[], options: SpawnOptions) => ChildProcessWithoutNullStreams
type ProcessKiller = typeof process.kill

const realProcessSpawner: ProcessSpawner = (command, args, options) =>
  spawn(command, args, options) as ChildProcessWithoutNullStreams

const processKillerDefault: ProcessKiller = process.kill

/**
 * Optional callbacks for capturing child process output line-by-line.
 * `onLine` is invoked with a single complete line (without the trailing
 * newline) as soon as it becomes available. stdout and stderr flow through
 * the same callback and share one ordered sequence — there is no stream
 * dimension. The emitter guarantees no line is lost:
 *
 *   1. A trailing partial line (a final write without `\n`) is flushed
 *      after the output pipes close.
 *   2. Direct-child exit terminates any descendants that retained inherited
 *      pipes; result completion waits for `close`, so bytes delivered between
 *      `exit` and `close` remain part of the aggregate output.
 *
 * `onClose` (optional) fires once after the drain, with the exit code,
 * and is the only place that observes the process terminator. Callers
 * that do not pass `onLine` see no behavioral change — the existing
 * aggregate `CommandResult` is byte-identical.
 *
 * `timeoutMs` (optional) arms a per-command timer layered over the
 * caller-supplied `AbortSignal`. Omitted or non-positive ⇒ no timer is
 * armed and the resolved result is byte-identical to a normal exit
 * (no `status` / `timeoutMs` keys). On expiry the child + its process
 * group are signaled, captured output up to the kill is preserved,
 * a sentinel line is appended to stderr, and the promise resolves with
 * `{ exitCode, stdout, stderr, status: "timeout", timeoutMs }`.
 */
export interface CommandLineOptions {
  onLine?: (line: string) => void
  onClose?: (exitCode: number) => void
  timeoutMs?: number
  /** Optional per-work containment bounds. Omitted means no containment machinery. */
  resourceLimits?: CommandResourceLimits
}

const ABORT_FORCE_KILL_GRACE_MS = 5_000
const RESOURCE_FORCE_KILL_GRACE_MS = 1_000

let prlimitAvailability: boolean | undefined
let processTreeRssReader: ProcessTreeRssReader = readProcessTreeRssBytes

export type ProcessTreeRssReader = (pid: number) => Promise<number | null>

/** Probe util-linux once per runner process. Non-Linux hosts use watchdog-only enforcement. */
export async function probePrlimit(): Promise<boolean> {
  if (prlimitAvailability !== undefined) return prlimitAvailability
  if (process.platform !== "linux") {
    prlimitAvailability = false
    return false
  }
  const environment = currentRunnerResources()?.environment ?? process.env
  if (environment.MOHIST_DISABLE_PRLIMIT === "1") {
    prlimitAvailability = false
    return false
  }
  try {
    assertExternalProcessAllowed("system/process.probePrlimit")
    const child = spawn("prlimit", ["--version"], { stdio: "ignore", shell: false })
    registerExternalProcess(child)
    prlimitAvailability = await new Promise<boolean>((resolve) => {
      child.once("error", () => resolve(false))
      child.once("close", (code) => resolve(code === 0))
    })
  } catch {
    prlimitAvailability = false
  }
  return prlimitAvailability
}

/** Test seam for forcing Linux fallback behavior without changing the host. */
export function setPrlimitAvailabilityForTests(value: boolean | undefined): void {
  prlimitAvailability = value
}

/** Test seam for deterministic RSS watchdog tests. */
export function setProcessTreeRssReaderForTests(reader: ProcessTreeRssReader | undefined): void {
  processTreeRssReader = reader ?? readProcessTreeRssBytes
}

function isPrlimitAvailable(): boolean {
  return prlimitAvailability === true
}

export async function ensureDir(path: string) {
  await currentRunnerFileSystem().ensureDir(path)
}

export function exists(path: string) {
  return currentRunnerFileSystem().exists(path)
}

export async function readText(path: string) {
  return await currentRunnerFileSystem().readText(path)
}

export async function writeText(path: string, content: string) {
  await currentRunnerFileSystem().writeText(path, content)
}

export async function writeBinary(path: string, content: Uint8Array) {
  await currentRunnerFileSystem().writeBinary(path, content)
}

export async function deleteFile(path: string) {
  await currentRunnerFileSystem().deleteFile(path)
}

export async function deleteDirectory(path: string) {
  await currentRunnerFileSystem().deleteDirectory(path)
}

export async function runCommand(
  command: string,
  args: string[],
  cwd: string,
  signal: AbortSignal,
  env?: NodeJS.ProcessEnv,
  options?: CommandLineOptions,
) {
  const scopedRunner = currentRunnerResources()?.commandRunner
  if (scopedRunner) {
    return await scopedRunner.run(command, args, cwd, signal, env, options) as CommandResult
  }
  const timeoutMs = options?.timeoutMs
  const onLine = options?.onLine
  const onClose = options?.onClose
  const resourceLimits = normalizeCommandResourceLimits(options?.resourceLimits)
  const memoryBytes = resourceLimits.memoryMb === null ? null : resourceLimits.memoryMb * 1024 * 1024
  const usePrlimit = memoryBytes !== null && memoryBytes !== undefined && isPrlimitAvailable()
  const spawnCommand = usePrlimit ? "prlimit" : command
  const spawnArgs = usePrlimit
    ? [`--as=${memoryBytes}`, `--data=${memoryBytes}`, "--", command, ...args]
    : args
  // Layer the per-command timer over the caller signal only when armed.
  // Omitted / non-positive ⇒ byte-identical behavior (no timer, no keys).
  const timeoutHandle = timeoutMs && timeoutMs > 0 ? createTimeoutSignal(signal, timeoutMs) : undefined
  const effectiveSignal = timeoutHandle?.signal ?? signal
  return await new Promise<CommandResult>((resolve, reject) => {
    // detached:true makes the child the leader of its own process group,
    // so on timeout / parent-abort we can signal the whole group via
    // process.kill(-pid) and reap helper processes (git-remote-http, ...)
    // alongside the direct child. We do NOT unref(): the parent still
    // awaits pipe closure, otherwise we'd race output drain and spawn errors.
    const scopedSpawner = currentRunnerResources()?.processSpawner
    if (!scopedSpawner) assertExternalProcessAllowed("system/process.runCommand")
    const processSpawner = scopedSpawner ?? realProcessSpawner
    const child = processSpawner(spawnCommand, spawnArgs, { cwd, env: { ...process.env, ...env }, signal: effectiveSignal, shell: false, detached: true })
    if (!scopedSpawner) registerExternalProcess(child)
    const stdout: Buffer[] = []
    const stderr: Buffer[] = []
    const stdoutState: LineBufferState = { carry: "", decoder: new StringDecoder("utf8") }
    const stderrState: LineBufferState = { carry: "", decoder: new StringDecoder("utf8") }
    // On timeout-vs-parent-abort distinction: the layered signal's reason
    // carries the timeout Error if and only if the per-command timer fired.
    // We consult it lazily from inside the error / close handlers so we
    // never race Node's internal abort listener.
    const wasTimeout = () => timeoutHandle?.timedOut() === true
    let completed = false
    let directExitCode: number | null | undefined
    let directExitSignal: NodeJS.Signals | null | undefined
    let containmentTriggered = false
    let forceKillTimer: NodeJS.Timeout | undefined
    const onAbort = () => {
      killProcess(child)
      forceKillTimer = setTimeout(() => killProcess(child, "SIGKILL"), ABORT_FORCE_KILL_GRACE_MS)
      forceKillTimer.unref()
    }
    const triggerContainment = () => {
      if (completed || containmentTriggered) return
      containmentTriggered = true
      killProcess(child)
      forceKillTimer = setTimeout(() => killProcess(child, "SIGKILL"), RESOURCE_FORCE_KILL_GRACE_MS)
      forceKillTimer.unref()
    }
    const watchdog = startResourceWatchdog(child, resourceLimits, triggerContainment)
    const cleanup = () => {
      effectiveSignal.removeEventListener("abort", onAbort)
      timeoutHandle?.dispose()
      watchdog.dispose()
    }
    const clearForceKillTimer = () => {
      if (forceKillTimer) clearTimeout(forceKillTimer)
      forceKillTimer = undefined
    }
    child.stdout.on("data", (chunk: Buffer) => {
      stdout.push(chunk)
      if (onLine) emitLines(stdoutState.decoder.write(chunk), stdoutState, onLine)
    })
    child.stderr.on("data", (chunk: Buffer) => {
      stderr.push(chunk)
      if (onLine) emitLines(stderrState.decoder.write(chunk), stderrState, onLine)
    })
    child.on("error", (error) => {
      // Node's spawn-time `signal` aborts the child via `abortChildProcess`
      // which emits `error` with an `AbortError`. We classify that abort:
      //   - timeout fired ⇒ swallow (close resolves with structured timeout)
      //   - parent aborted ⇒ reject (today's behavior, unchanged)
      if (wasTimeout()) return
      if (completed) return
      completed = true
      cleanup()
      reject(error)
    })
    // Group-kill helper processes (git-remote-http, …) alongside the direct
    // child. Node's `signal` option only kills the direct child, so an
    // explicit process-group kill is required on both abort paths.
    effectiveSignal.addEventListener("abort", onAbort, { once: true })
    const complete = (code: number | null) => {
      if (completed) return
      completed = true
      const exitCode = code ?? 1
      const timedOut = wasTimeout()
      const abnormalContainment = containmentTriggered || isResourceLimitExit(usePrlimit, exitCode, directExitSignal)
      cleanup()
      if (onLine) {
        emitLines(stdoutState.decoder.end(), stdoutState, onLine)
        emitLines(stderrState.decoder.end(), stderrState, onLine)
        // Post-close drain: flush any buffered tail that did not end with a newline.
        // Single emission per stream so we never duplicate the tail.
        drainTail(stdoutState, onLine)
        drainTail(stderrState, onLine)
      }
      if (onClose) onClose(exitCode)
      const stdoutText = Buffer.concat(stdout).toString("utf8")
      const stderrText = Buffer.concat(stderr).toString("utf8")
      const contained = abnormalContainment || isResourceLimitOutput(usePrlimit, stdoutText, stderrText)
      child.stdout.destroy?.()
      child.stderr.destroy?.()
      if (timedOut) {
        // Structured timeout result. The sentinel `Command timed out after Ns`
        // matches the unchanged `looksLikeRetrySafe` arm in
        // `actions/github-pr-classify.ts` so the classifier absorbs the
        // timeout as `retry-safe` with no rule additions.
        const sentinel = `Command timed out after ${timeoutMs! / 1000}s\n`
        resolve({
          exitCode,
          stdout: stdoutText,
          stderr: stderrText + sentinel,
          status: "timeout",
          timeoutMs: timeoutMs!,
        })
        return
      }
      if (contained) {
        resolve({
          exitCode,
          stdout: stdoutText,
          stderr: stderrText + "Command terminated by resource containment\n",
          resourceContainment: true,
        })
        return
      }
      resolve({ exitCode, stdout: stdoutText, stderr: stderrText })
    }
    child.once("exit", (code, signal) => {
      directExitCode = code
      directExitSignal = signal
      // A descendant that inherited stdout/stderr can keep `close` from firing
      // after the direct child exits. It belongs to this command tree and must
      // not outlive the command or write into a later work item.
      killProcess(child, "SIGKILL")
    })
    child.once("close", (code) => {
      clearForceKillTimer()
      complete(directExitCode ?? code)
    })
  })
}

interface NormalizedCommandResourceLimits {
  readonly memoryMb: number | null
  readonly wallClockMs: number | null
  readonly watchdogIntervalMs: number
}

function normalizeCommandResourceLimits(value: CommandResourceLimits | undefined): NormalizedCommandResourceLimits {
  return {
    memoryMb: positiveLimit(value?.memoryMb),
    wallClockMs: positiveLimit(value?.wallClockMs),
    watchdogIntervalMs: positiveLimit(value?.watchdogIntervalMs) ?? 250,
  }
}

function positiveLimit(value: number | null | undefined): number | null {
  if (value === null || value === undefined) return null
  return Number.isFinite(value) && value > 0 ? Math.max(1, Math.floor(value)) : null
}

function isResourceLimitExit(usePrlimit: boolean, exitCode: number, signal: NodeJS.Signals | null | undefined): boolean {
  if (!usePrlimit) return false
  return (signal !== null && signal !== undefined)
    || exitCode === 133 || exitCode === 134 || exitCode === 137 || exitCode === 139
}

function isResourceLimitOutput(usePrlimit: boolean, stdout: string, stderr: string): boolean {
  if (!usePrlimit) return false
  return /out of memory|memoryerror|cannot allocate|allocation failed|array buffer allocation failed|trace\/breakpoint trap|fatal error|enomem/i.test(`${stdout}\n${stderr}`)
}

function startResourceWatchdog(
  child: ChildProcess,
  limits: NormalizedCommandResourceLimits,
  onContainment: () => void,
): { dispose: () => void } {
  if (limits.memoryMb === null && limits.wallClockMs === null) return { dispose: () => {} }

  let disposed = false
  let checking = false
  const interval = limits.memoryMb === null
    ? undefined
    : setInterval(() => {
        if (disposed || checking || child.pid === undefined) return
        checking = true
        void processTreeRssReader(child.pid)
          .then((rssBytes) => {
            if (!disposed && rssBytes !== null && rssBytes > limits.memoryMb! * 1024 * 1024) onContainment()
          })
          .catch(() => undefined)
          .finally(() => { checking = false })
      }, limits.watchdogIntervalMs)
  interval?.unref?.()

  const wallClock = limits.wallClockMs === null
    ? undefined
    : setTimeout(onContainment, limits.wallClockMs)
  wallClock?.unref?.()

  return {
    dispose: () => {
      if (disposed) return
      disposed = true
      if (interval !== undefined) clearInterval(interval)
      if (wallClock !== undefined) clearTimeout(wallClock)
    },
  }
}

/** Sum VmRSS for a process and its descendants on Linux. */
export async function readProcessTreeRssBytes(pid: number): Promise<number | null> {
  const pending = [pid]
  const seen = new Set<number>()
  let total = 0
  let found = false
  while (pending.length > 0) {
    const current = pending.pop()!
    if (seen.has(current)) continue
    seen.add(current)
    const [status, children] = await Promise.all([
      readFile(`/proc/${current}/status`, "utf8").catch(() => null),
      readFile(`/proc/${current}/task/${current}/children`, "utf8").catch(() => null),
    ])
    if (status !== null) {
      const match = /^VmRSS:\s+(\\d+)\\s+kB$/m.exec(status)
      if (match) {
        total += Number(match[1]) * 1024
        found = true
      }
    }
    if (children !== null) {
      for (const value of children.trim().split(/\\s+/)) {
        const childPid = Number(value)
        if (Number.isInteger(childPid) && childPid > 0) pending.push(childPid)
      }
    }
  }
  return found ? total : null
}

interface LineBufferState {
  carry: string
  decoder: StringDecoder
}

function emitLines(chunk: string, state: LineBufferState, onLine: (line: string) => void) {
  const combined = state.carry + chunk
  let start = 0
  for (let i = 0; i < combined.length; i++) {
    if (combined.charCodeAt(i) === 10 /* \n */) {
      const raw = combined.slice(start, i)
      // Strip a preceding \r so Windows CRLF preserves the line content;
      // the runtime normalises boundaries, not line contents.
      const line = raw.endsWith("\r") ? raw.slice(0, -1) : raw
      onLine(line)
      start = i + 1
    }
  }
  state.carry = combined.slice(start)
}

function drainTail(state: LineBufferState, onLine: (line: string) => void) {
  if (state.carry.length === 0) return
  onLine(state.carry)
  state.carry = ""
}

export async function copyDirectory(source: string, destination: string) {
  await currentRunnerFileSystem().copyDirectory(source, destination)
}

/**
 * Signal the child. When the child was spawned detached (it leads its
 * own process group), prefer `process.kill(-pid)` to reap helper
 * processes alongside the direct child. On Windows (no negative-PID
 * support) or when the group kill fails (including non-detached children),
 * fall back to `child.kill(sig)` so the direct child is still signaled.
 */
export function killProcess(child: ChildProcess, signal: NodeJS.Signals = "SIGTERM") {
  if (child.pid !== undefined && process.platform !== "win32") {
    try {
      const processKiller = currentRunnerResources()?.processKiller ?? processKillerDefault
      processKiller(-child.pid, signal)
      return
    } catch {
      // A non-detached child has no process group whose id equals child.pid;
      // Signal the direct child when its process group is unavailable.
    }
  }
  try {
    child.kill(signal)
  } catch {
    // The process may have already exited.
  }
}

export function sanitizedEnvironment(env?: NodeJS.ProcessEnv) {
  const next = { ...process.env, ...env }
  delete next.OPENCODE_SERVER_PASSWORD
  delete next.OPENCODE_SERVER_USERNAME
  next.OPENCODE_DISABLE_UPDATE_CHECK = next.OPENCODE_DISABLE_UPDATE_CHECK ?? "1"
  next.OPENCODE_DISABLE_AUTO_UPDATE = next.OPENCODE_DISABLE_AUTO_UPDATE ?? "1"
  next.NO_UPDATE_NOTIFIER = next.NO_UPDATE_NOTIFIER ?? "1"
  return next
}
