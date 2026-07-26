import { join } from "node:path"
import { randomUUID } from "node:crypto"
import type { ActionResult, JsonObject } from "../core/types.js"
import type { ActionHost } from "./host.js"
import { arrayInput, numberInput, stringInput } from "../core/json.js"
import { deleteFile, exists, readText, runCommand, writeText, type CommandLineOptions } from "../system/process.js"
import { timeoutSignal } from "../system/timeout-signal.js"
import { resolveActionPath } from "./expectations.js"
import { fail, succeed } from "./action-result.js"

export async function processAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const command = stringInput(inputs, "command")
  if (!command) return fail("invalid-input", "Process action requires command")
  const result = await runCommand(command, arrayInput(inputs, "args").map(String), host.workDir, host.signal, undefined, logLineOptions(host, "action:process"))
  if (result.exitCode === 0) {
    const output: JsonObject = {
      stdout: result.stdout.trim(),
      exitCode: result.exitCode,
    }
    return succeed(output, { exitCode: result.exitCode })
  }
  return fail("process-failed", result.stderr.trim() || `Process exited with code ${result.exitCode}`, { exitCode: result.exitCode })
}

export async function scriptAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const run = stringInput(inputs, "run")
  if (!run?.trim()) return fail("invalid-input", "Script action requires 'run'")
  const shell = stringInput(inputs, "shell") || (process.platform === "win32" ? "pwsh" : "bash")
  const file = join(host.workDir, `_${randomUUID().replace(/-/g, "")}${process.platform === "win32" ? ".ps1" : ".sh"}`)
  await writeText(file, run)
  try {
    const timeoutMs = numberInput(inputs, "timeout")
    const signal = timeoutMs ? timeoutSignal(host.signal, timeoutMs) : host.signal
    const result = await runCommand(shell, [file], host.workDir, signal, undefined, logLineOptions(host, "action:script"))
    if (result.exitCode !== 0) {
      return fail(result.status === "timeout" ? "timeout" : "script-failed", `Script failed: ${firstLine(run)}${result.stderr.trim() ? `: ${trim(result.stderr)}` : ""}`, { exitCode: result.exitCode })
    }
    const output: JsonObject = { kind: "script", run, shell, exitCode: result.exitCode, stdout: trim(result.stdout), stderr: trim(result.stderr) }
    return succeed(output, { exitCode: result.exitCode })
  } finally {
    await deleteFile(file)
  }
}

export async function artifactExistsAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const path = resolveActionPath(host.workDir, stringInput(inputs, "path"))
  if (!path) return fail("invalid-input", "Artifact check requires 'path'")
  const found = exists(path)
  const output: JsonObject = { kind: "artifact-exists", path, exists: found }
  return found ? succeed(output) : fail("artifact-missing", `Artifact missing: ${path}`)
}

export async function markerAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  const path = resolveActionPath(host.workDir, stringInput(inputs, "path"))
  const expect = stringInput(inputs, "expect")
  if (!path || !expect) return fail("invalid-input", "Marker check requires 'path' and 'expect'")
  if (!exists(path)) return fail("artifact-missing", `Marker file missing: ${path}`)
  const content = await readText(path)
  const found = matchesMarker(content, expect)
  const output: JsonObject = { kind: "marker", path, marker: expect, found }
  return found ? succeed(output) : fail("marker-missing", `Marker missing in ${path}`)
}

function matchesMarker(content: string, expect: string) {
  if (isPromiseVerdict(expect)) {
    const verdicts = [...content.matchAll(/<promise>\s*(PASS|FAIL)\s*<\/promise>/g)].map((match) => `<promise>${match[1]}</promise>`)
    return verdicts.length === 1 && verdicts[0] === expect
  }

  return content.includes(expect)
}

function isPromiseVerdict(value: string) {
  return /^<promise>\s*(PASS|FAIL)\s*<\/promise>$/.test(value)
}

function firstLine(value: string) {
  return value.replace(/\r\n/g, "\n").trim().split("\n")[0]
}

function trim(value: string) {
  return value.length <= 20_000 ? value : value.slice(0, 20_000)
}

function logLineOptions(host: ActionHost, source: string): CommandLineOptions | undefined {
  return host.log ? { onLine: (line) => host.log!.write(source, line) } : undefined
}
