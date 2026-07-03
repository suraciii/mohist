import { spawn } from "node:child_process"
import type { ChildProcess } from "node:child_process"
import { mkdir, readFile, rm, writeFile } from "node:fs/promises"
import { existsSync } from "node:fs"
import { dirname } from "node:path"
import { StringDecoder } from "node:string_decoder"

export interface CommandResult {
  exitCode: number
  stdout: string
  stderr: string
}

/**
 * Optional callbacks for capturing child process output line-by-line.
 * `onLine` is invoked with a single complete line (without the trailing
 * newline) as soon as it becomes available. stdout and stderr flow through
 * the same callback and share one ordered sequence — there is no stream
 * dimension. The emitter guarantees no line is lost:
 *
 *   1. A trailing partial line (a final write without `\n`) is flushed
 *      once the process closes.
 *   2. A post-exit drain emits any buffered tail once after `close`,
 *      so any data already buffered at the moment the child exits is
 *      delivered before the promise resolves.
 *
 * `onClose` (optional) fires once after the drain, with the exit code,
 * and is the only place that observes the process terminator. Callers
 * that do not pass `onLine` see no behavioral change — the existing
 * aggregate `CommandResult` is byte-identical.
 */
export interface CommandLineOptions {
  onLine?: (line: string) => void
  onClose?: (exitCode: number) => void
}

export async function ensureDir(path: string) {
  await mkdir(path, { recursive: true })
}

export function exists(path: string) {
  return existsSync(path)
}

export async function readText(path: string) {
  return await readFile(path, "utf8")
}

export async function writeText(path: string, content: string) {
  await mkdir(dirname(path), { recursive: true })
  await writeFile(path, content)
}

export async function deleteFile(path: string) {
  await rm(path, { force: true })
}

export async function deleteDirectory(path: string) {
  await rm(path, { recursive: true, force: true })
}

export async function runCommand(
  command: string,
  args: string[],
  cwd: string,
  signal: AbortSignal,
  env?: NodeJS.ProcessEnv,
  options?: CommandLineOptions,
) {
  return await new Promise<CommandResult>((resolve, reject) => {
    const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, signal, shell: false })
    const stdout: Buffer[] = []
    const stderr: Buffer[] = []
    const onLine = options?.onLine
    const onClose = options?.onClose
    const stdoutState: LineBufferState = { carry: "", decoder: new StringDecoder("utf8") }
    const stderrState: LineBufferState = { carry: "", decoder: new StringDecoder("utf8") }
    child.stdout.on("data", (chunk: Buffer) => {
      stdout.push(chunk)
      if (onLine) emitLines(stdoutState.decoder.write(chunk), stdoutState, onLine)
    })
    child.stderr.on("data", (chunk: Buffer) => {
      stderr.push(chunk)
      if (onLine) emitLines(stderrState.decoder.write(chunk), stderrState, onLine)
    })
    child.on("error", reject)
    child.on("close", (code) => {
      const exitCode = code ?? 1
      if (onLine) {
        emitLines(stdoutState.decoder.end(), stdoutState, onLine)
        emitLines(stderrState.decoder.end(), stderrState, onLine)
        // Post-exit drain: flush any buffered tail that did not end with a newline.
        // Single emission per stream so we never duplicate the tail.
        drainTail(stdoutState, onLine)
        drainTail(stderrState, onLine)
      }
      if (onClose) onClose(exitCode)
      resolve({ exitCode, stdout: Buffer.concat(stdout).toString("utf8"), stderr: Buffer.concat(stderr).toString("utf8") })
    })
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
  await mkdir(destination, { recursive: true })
  const { cp } = await import("node:fs/promises")
  await cp(source, destination, { recursive: true, force: true })
}

export function killProcess(child: ChildProcess) {
  try {
    child.kill("SIGTERM")
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
