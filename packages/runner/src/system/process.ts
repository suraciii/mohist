import { spawn } from "node:child_process"
import type { ChildProcess, ChildProcessWithoutNullStreams, SpawnOptions } from "node:child_process"
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
  /**
   * The per-command timeout (ms) that fired. Absent on normal exit.
   */
  timeoutMs?: number
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
 *      when the direct child exits.
 *   2. A post-exit drain emits any buffered tail once after `exit`,
 *      so any data already buffered at the moment the child exits is
 *      delivered before the promise resolves.
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
}

const ABORT_FORCE_KILL_GRACE_MS = 5_000

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
  // Layer the per-command timer over the caller signal only when armed.
  // Omitted / non-positive ⇒ byte-identical behavior (no timer, no keys).
  const timeoutHandle = timeoutMs && timeoutMs > 0 ? createTimeoutSignal(signal, timeoutMs) : undefined
  const effectiveSignal = timeoutHandle?.signal ?? signal
  return await new Promise<CommandResult>((resolve, reject) => {
    // detached:true makes the child the leader of its own process group,
    // so on timeout / parent-abort we can signal the whole group via
    // process.kill(-pid) and reap helper processes (git-remote-http, ...)
    // alongside the direct child. We do NOT unref(): the parent still
    // awaits the direct child's exit, otherwise we'd race the spawn-error path.
    const scopedSpawner = currentRunnerResources()?.processSpawner
    if (!scopedSpawner) assertExternalProcessAllowed("system/process.runCommand")
    const processSpawner = scopedSpawner ?? realProcessSpawner
    const child = processSpawner(command, args, { cwd, env: { ...process.env, ...env }, signal: effectiveSignal, shell: false, detached: true })
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
    const onAbort = () => {
      killProcess(child)
      const forceKillTimer = setTimeout(() => killProcess(child, "SIGKILL"), ABORT_FORCE_KILL_GRACE_MS)
      forceKillTimer.unref()
    }
    const cleanup = () => {
      effectiveSignal.removeEventListener("abort", onAbort)
      timeoutHandle?.dispose()
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
      cleanup()
      reject(error)
    })
    // Group-kill helper processes (git-remote-http, …) alongside the direct
    // child. Node's `signal` option only kills the direct child, so an
    // explicit process-group kill is required on both abort paths.
    effectiveSignal.addEventListener("abort", onAbort, { once: true })
    let completed = false
    const complete = (code: number | null) => {
      if (completed) return
      completed = true
      const exitCode = code ?? 1
      const timedOut = wasTimeout()
      cleanup()
      if (onLine) {
        emitLines(stdoutState.decoder.end(), stdoutState, onLine)
        emitLines(stderrState.decoder.end(), stderrState, onLine)
        // Post-exit drain: flush any buffered tail that did not end with a newline.
        // Single emission per stream so we never duplicate the tail.
        drainTail(stdoutState, onLine)
        drainTail(stderrState, onLine)
      }
      if (onClose) onClose(exitCode)
      const stdoutText = Buffer.concat(stdout).toString("utf8")
      const stderrText = Buffer.concat(stderr).toString("utf8")
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
      resolve({ exitCode, stdout: stdoutText, stderr: stderrText })
    }
    child.once("exit", complete)
    child.once("close", complete)
  })
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
