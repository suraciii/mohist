import { spawn } from "node:child_process"
import type { ChildProcess } from "node:child_process"
import { mkdir, readFile, rm, writeFile } from "node:fs/promises"
import { existsSync } from "node:fs"
import { dirname } from "node:path"

export interface CommandResult {
  exitCode: number
  stdout: string
  stderr: string
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

export async function runCommand(command: string, args: string[], cwd: string, signal: AbortSignal, env?: NodeJS.ProcessEnv) {
  return await new Promise<CommandResult>((resolve, reject) => {
    const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, signal, shell: false })
    const stdout: Buffer[] = []
    const stderr: Buffer[] = []
    child.stdout.on("data", (chunk: Buffer) => stdout.push(chunk))
    child.stderr.on("data", (chunk: Buffer) => stderr.push(chunk))
    child.on("error", reject)
    child.on("close", (code) => resolve({ exitCode: code ?? 1, stdout: Buffer.concat(stdout).toString("utf8"), stderr: Buffer.concat(stderr).toString("utf8") }))
  })
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
  return next
}
