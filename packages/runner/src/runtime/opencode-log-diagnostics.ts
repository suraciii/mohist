import { homedir } from "node:os"
import { join } from "node:path"
import { currentRunnerFileSystem, currentRunnerResources } from "../system/filesystem.js"

const MAX_LOG_FILES = 20
const MAX_LOG_FILE_BYTES = 10 * 1024 * 1024
const TAIL_BYTES = 4 * 1024 * 1024
const MAX_MESSAGE_LENGTH = 500

export interface OpencodeProviderErrorDiagnostic {
  sessionId: string
  summary: string
  providerId?: string
  modelId?: string
  agent?: string
  statusCode?: number
  errorName?: string
  errorType?: string
  message?: string
  retryable?: boolean
  logFile?: string
  logLine?: number
  occurredAt?: string
}

export interface OpencodeProviderErrorSearchOptions {
  sinceMs?: number
  failFastOnly?: boolean
}

export type OpencodeProviderErrorDiagnosticFinder = (sessionId: string, options: OpencodeProviderErrorSearchOptions) => Promise<OpencodeProviderErrorDiagnostic | undefined>

export interface OpencodeLogFileSystem {
  readdir(path: string): Promise<string[]>
  stat(path: string): Promise<{ isFile(): boolean; mtimeMs: number; size: number }>
  readFile(path: string): Promise<string>
  readTail(path: string, start: number, length: number): Promise<string>
}

interface CandidateLogFile {
  path: string
  mtimeMs: number
  size: number
}

export async function findOpencodeProviderErrorDiagnostic(sessionId: string, options: OpencodeProviderErrorSearchOptions = {}): Promise<OpencodeProviderErrorDiagnostic | undefined> {
  if (!sessionId.trim()) return undefined
  const resources = currentRunnerResources()
  if (resources?.opencodeProviderErrorDiagnosticFinder) return await resources.opencodeProviderErrorDiagnosticFinder(sessionId, options)

  const fileSystem = resources?.opencodeLogFileSystem ?? nodeOpencodeLogFileSystem
  const logDir = opencodeLogDir()
  let entries: string[]
  try {
    entries = await fileSystem.readdir(logDir)
  } catch {
    return undefined
  }

  const files: CandidateLogFile[] = []
  for (const entry of entries) {
    if (!entry.endsWith(".log")) continue
    const path = join(logDir, entry)
    try {
      const info = await fileSystem.stat(path)
      if (!info.isFile()) continue
      files.push({ path, mtimeMs: info.mtimeMs, size: info.size })
    } catch {}
  }

  files.sort((a, b) => b.mtimeMs - a.mtimeMs)
  for (const file of files.slice(0, MAX_LOG_FILES)) {
    const found = await findDiagnosticInLogFile(file, sessionId, options)
    if (found) return found
  }

  return undefined
}

export async function findFailFastOpencodeProviderErrorDiagnostic(sessionId: string, sinceMs: number): Promise<OpencodeProviderErrorDiagnostic | undefined> {
  return await findOpencodeProviderErrorDiagnostic(sessionId, { sinceMs, failFastOnly: true })
}

export function isFailFastOpencodeProviderError(diagnostic: OpencodeProviderErrorDiagnostic): boolean {
  const haystack = [
    diagnostic.errorName,
    diagnostic.errorType,
    diagnostic.message,
    diagnostic.summary,
  ].filter(Boolean).join(" ").toLowerCase()

  if (diagnostic.statusCode && [400, 401, 402, 403, 404].includes(diagnostic.statusCode)) return true
  if (/\bai_loadapikeyerror\b/i.test(diagnostic.errorName ?? "")) return true

  return [
    /token plan/,
    /usage limit/,
    /quota/,
    /credit/,
    /billing/,
    /payment required/,
    /api[_ -]?key/,
    /auth(?:entication|orization)? failed/,
    /unauthorized/,
    /forbidden/,
    /permission denied/,
    /model .*not .*found/,
    /model .*not .*available/,
    /model .*unavailable/,
    /model .*does not exist/,
    /unsupported model/,
    /invalid model/,
  ].some((pattern) => pattern.test(haystack))
}

export function appendOpencodeDiagnostic(message: string, diagnostic: OpencodeProviderErrorDiagnostic | undefined) {
  if (!diagnostic) return message
  if (message.includes(diagnostic.summary)) return message
  return `${message}\n${diagnostic.summary}`
}

async function findDiagnosticInLogFile(file: CandidateLogFile, sessionId: string, options: OpencodeProviderErrorSearchOptions): Promise<OpencodeProviderErrorDiagnostic | undefined> {
  const text = await readLogFileText(file)
  if (!text) return undefined

  const lines = text.split(/\r?\n/)

  for (let index = lines.length - 1; index >= 0; index -= 1) {
    const line = lines[index]
    if (!isProviderErrorLine(line)) continue
    if (!line.includes(`session.id=${sessionId}`)) continue
    const diagnostic = parseProviderErrorLine(line, sessionId, file.path, index + 1)
    if (!matchesSearchOptions(diagnostic, options)) continue
    return diagnostic
  }

  return undefined
}

function matchesSearchOptions(diagnostic: OpencodeProviderErrorDiagnostic, options: OpencodeProviderErrorSearchOptions): boolean {
  if (options.sinceMs !== undefined) {
    if (!diagnostic.occurredAt) return false
    const occurredAtMs = Date.parse(diagnostic.occurredAt)
    if (!Number.isFinite(occurredAtMs) || occurredAtMs < options.sinceMs) return false
  }
  if (options.failFastOnly && !isFailFastOpencodeProviderError(diagnostic)) return false
  return true
}

async function readLogFileText(file: CandidateLogFile): Promise<string | undefined> {
  const fileSystem = currentRunnerResources()?.opencodeLogFileSystem ?? nodeOpencodeLogFileSystem
  if (file.size <= MAX_LOG_FILE_BYTES) {
    try {
      return await fileSystem.readFile(file.path)
    } catch {
      return undefined
    }
  }
  try {
    const start = Math.max(0, file.size - TAIL_BYTES)
    let text = await fileSystem.readTail(file.path, start, file.size - start)
    if (start > 0) {
      const firstNewline = text.indexOf("\n")
      if (firstNewline >= 0) text = text.slice(firstNewline + 1)
    }
    return text
  } catch {
    return undefined
  }
}

const nodeOpencodeLogFileSystem: OpencodeLogFileSystem = {
  async readdir(path) {
    return (await currentRunnerFileSystem().readdir(path)).map((entry) => entry.name)
  },
  async stat(path) {
    const value = await currentRunnerFileSystem().stat(path)
    return { isFile: value.isFile, mtimeMs: value.mtimeMs, size: value.size }
  },
  async readFile(path) {
    return await currentRunnerFileSystem().readText(path)
  },
  async readTail(path, start, length) {
    return await currentRunnerFileSystem().readTail(path, start, length)
  },
}

function isProviderErrorLine(line: string): boolean {
  if (!line.includes("ERROR")) return false
  if (line.includes('message="stream error"') && line.includes("error.error=")) return true
  if (line.includes("service=llm") && line.includes("error=")) return true
  return false
}


function parseProviderErrorLine(line: string, sessionId: string, logFile: string, logLine: number): OpencodeProviderErrorDiagnostic {
  const providerId = field(line, "providerID")
  const modelId = field(line, "modelID")
  const agent = field(line, "agent")
  const occurredAt = extractTimestamp(line)

  const errorText = logfmtQuotedField(line, "error.error")
  if (errorText) {
    const { name, message } = parseErrorText(errorText)
    return {
      sessionId,
      summary: formatSummary({ providerId, modelId, errorName: name, message }),
      providerId,
      modelId,
      agent,
      errorName: name,
      message,
      logFile,
      logLine,
      occurredAt,
    }
  }

  const statusCode = numberField(line, "statusCode")
  const errorName = jsonStringField(line, "name")
  const retryable = booleanField(line, "isRetryable")
  const responseBody = jsonStringField(line, "responseBody")
  const responseError = parseResponseBody(responseBody)
  const errorType = responseError?.type ?? jsonStringField(line, "type")
  const message = sanitizeMessage(responseError?.message)

  return {
    sessionId,
    summary: formatSummary({ providerId, modelId, statusCode, errorType, message, errorName }),
    providerId,
    modelId,
    agent,
    statusCode,
    errorName,
    errorType,
    message,
    retryable,
    logFile,
    logLine,
    occurredAt,
  }
}

function opencodeLogDir() {
  const environment = currentRunnerResources()?.environment ?? process.env
  if (environment.MOHIST_OPENCODE_LOG_DIR?.trim()) return environment.MOHIST_OPENCODE_LOG_DIR
  if (environment.OPENCODE_LOG_DIR?.trim()) return environment.OPENCODE_LOG_DIR
  const dataHome = environment.XDG_DATA_HOME?.trim() || join(homedir(), ".local", "share")
  return join(dataHome, "opencode", "log")
}

function field(line: string, name: string) {
  return line.match(new RegExp(`\\b${escapeRegExp(name)}=([^\\s]+)`))?.[1]
}

function numberField(line: string, name: string) {
  const value = line.match(new RegExp(`"${escapeRegExp(name)}":(\\d+)`))?.[1]
  return value ? Number(value) : undefined
}

function booleanField(line: string, name: string) {
  const value = line.match(new RegExp(`"${escapeRegExp(name)}":(true|false)`))?.[1]
  return value === undefined ? undefined : value === "true"
}

function jsonStringField(line: string, name: string) {
  const match = line.match(new RegExp(`"${escapeRegExp(name)}":"((?:\\\\.|[^"\\\\])*)"`))
  if (!match) return undefined
  try {
    return JSON.parse(`"${match[1]}"`) as string
  } catch {
    return undefined
  }
}

function logfmtQuotedField(line: string, name: string): string | undefined {
  const match = line.match(new RegExp(`\\b${escapeRegExp(name)}="((?:[^"\\\\]|\\\\.)*)"`))
  if (!match) return undefined
  try {
    return JSON.parse(`"${match[1]}"`) as string
  } catch {
    return match[1]
  }
}

function parseResponseBody(value: string | undefined) {
  if (!value) return undefined
  try {
    const parsed = JSON.parse(value) as { error?: { type?: unknown; message?: unknown } }
    const type = typeof parsed.error?.type === "string" ? parsed.error.type : undefined
    const message = typeof parsed.error?.message === "string" ? parsed.error.message : undefined
    return type || message ? { type, message } : undefined
  } catch {
    return undefined
  }
}

function parseErrorText(text: string): { name?: string; message?: string } {
  const colonIndex = text.indexOf(": ")
  if (colonIndex < 0) return { name: text.trim() || undefined, message: undefined }
  const name = text.slice(0, colonIndex).trim()
  return { name: name || undefined, message: sanitizeMessage(text.slice(colonIndex + 2).trim()) }
}

function extractTimestamp(line: string): string | undefined {
  return field(line, "timestamp") ?? line.match(/^ERROR\s+(\S+)/)?.[1]
}

function sanitizeMessage(value: string | undefined) {
  if (!value) return undefined
  const normalized = value.replace(/\s+/g, " ").trim()
  if (normalized.length <= MAX_MESSAGE_LENGTH) return normalized
  return `${normalized.slice(0, MAX_MESSAGE_LENGTH - 3)}...`
}

function formatSummary(input: {
  providerId?: string
  modelId?: string
  statusCode?: number
  errorType?: string
  message?: string
  errorName?: string
}) {
  const model = [input.providerId, input.modelId].filter(Boolean).join("/") || "unknown model"
  const status = input.statusCode ? ` ${input.statusCode}` : ""
  const type = input.errorType ? ` ${input.errorType}` : input.errorName ? ` ${input.errorName}` : ""
  const message = input.message ? ` - ${input.message}` : ""
  return `Opencode provider error:${status}${type} on ${model}${message}`
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")
}
