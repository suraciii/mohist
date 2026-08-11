import { homedir } from "node:os"
import { join } from "node:path"
import { currentRunnerFileSystem, currentRunnerResources } from "./filesystem.js"

export const RUNNER_LOG_MAX_BYTES = 32 * 1024 * 1024

export type LogLevel = "TRACE" | "DEBUG" | "INFO" | "WARN" | "ERROR" | "FATAL"
export type LogFields = Record<string, unknown>

export interface LogFileWriter {
  ensureDirectory(directory: string): Promise<void>
  size(path: string): Promise<number>
  append(path: string, content: string): Promise<void>
  rename(source: string, destination: string): Promise<boolean>
}

export interface LogTerminal {
  write(line: string): void
}

export interface RunnerLoggerOptions {
  clock?: () => Date
  logsPath?: string
  environment?: Record<string, string | undefined>
  homeDirectory?: string
  fileWriter?: LogFileWriter
  terminal?: LogTerminal
  maxBytes?: number
}

export interface RunnerLogger {
  trace(message: string, fields?: LogFields): void
  debug(message: string, fields?: LogFields): void
  info(message: string, fields?: LogFields): void
  warn(message: string, fields?: LogFields): void
  error(message: string, fields?: LogFields): void
  fatal(message: string, fields?: LogFields): void
  child(component: string): RunnerLogger
  flush(): Promise<void>
}

const nodeFileWriter: LogFileWriter = {
  async ensureDirectory(directory) {
    await currentRunnerFileSystem().ensureDir(directory)
  },
  async size(path) {
    try {
      return (await currentRunnerFileSystem().stat(path)).size
    } catch (error) {
      if (isNotFound(error)) return 0
      throw error
    }
  },
  async append(path, content) {
    await currentRunnerFileSystem().appendText(path, content)
  },
  async rename(source, destination) {
    try {
      await currentRunnerFileSystem().rename(source, destination)
      return true
    } catch (error) {
      if (isNotFound(error)) return false
      throw error
    }
  },
}

const processTerminal: LogTerminal = {
  write(line) {
    process.stderr.write(line)
  },
}

export function resolveRunnerLogsDirectory(
  environment: Record<string, string | undefined> = process.env,
  homeDirectory = homedir(),
): string {
  return environment.MOHIST_LOGS_PATH || join(homeDirectory, ".mohist", "logs")
}

export function createRunnerLogger(options: RunnerLoggerOptions = {}): RunnerLogger {
  const logsPath = options.logsPath ?? resolveRunnerLogsDirectory(options.environment, options.homeDirectory)
  const filePath = join(logsPath, "runner.log")
  const clock = options.clock ?? (() => new Date())
  const writer = options.fileWriter ?? nodeFileWriter
  const terminal = options.terminal ?? processTerminal
  const maxBytes = options.maxBytes ?? RUNNER_LOG_MAX_BYTES
  let directoryReady = false
  let pending = Promise.resolve()

  const writeLine = async (line: string): Promise<void> => {
    if (!directoryReady) {
      await writer.ensureDirectory(logsPath)
      directoryReady = true
    }
    const lineBytes = Buffer.byteLength(line, "utf8")
    if (await writer.size(filePath) + lineBytes > maxBytes) {
      await writer.rename(`${filePath}.1`, `${filePath}.2`)
      await writer.rename(filePath, `${filePath}.1`)
    }
    await writer.append(filePath, line)
  }

  const emit = (level: LogLevel, component: string, message: string, fields?: LogFields): void => {
    let line: string
    try {
      line = formatLogLine(clock(), level, message, component, fields)
    } catch {
      return
    }
    try {
      terminal.write(line)
    } catch {
    }
    pending = pending.then(() => writeLine(line), () => writeLine(line)).catch(() => undefined)
  }

  const createChild = (component: string): RunnerLogger => ({
    trace: (message, fields) => emit("TRACE", component, message, fields),
    debug: (message, fields) => emit("DEBUG", component, message, fields),
    info: (message, fields) => emit("INFO", component, message, fields),
    warn: (message, fields) => emit("WARN", component, message, fields),
    error: (message, fields) => emit("ERROR", component, message, fields),
    fatal: (message, fields) => emit("FATAL", component, message, fields),
    child: createChild,
    flush: () => pending,
  })

  return createChild("runner")
}

const RESERVED_FIELD_KEYS = new Set(["time", "level", "msg", "service", "component"])

function formatLogLine(date: Date, level: LogLevel, message: string, component: string, fields?: LogFields): string {
  const values: Array<[string, unknown]> = [
    ["time", date.toISOString()],
    ["level", level],
    ["msg", message],
    ["service", "runner"],
    ["component", component],
  ]
  for (const [key, value] of Object.entries(fields ?? {})) {
    if (RESERVED_FIELD_KEYS.has(key)) continue
    values.push([key, value])
  }
  return values
    .flatMap(([key, value]) => {
      const formatted = formatFieldValue(key, value)
      return formatted === null ? [] : [`${key}=${formatted}`]
    })
    .join(" ") + "\n"
}

function formatFieldValue(key: string, value: unknown): string | null {
  if (value === undefined || value === null) return null
  if (key === "exception" || value instanceof Error) return quoteLogfmt(formatException(value))
  if (typeof value === "string") return formatString(value)
  if (typeof value === "number" || typeof value === "boolean" || typeof value === "bigint") return String(value)
  if (typeof value === "object") {
    try {
      const serialized = JSON.stringify(value)
      return serialized === undefined ? null : formatString(serialized)
    } catch {
      return null
    }
  }
  return formatString(String(value))
}

function formatException(value: unknown): string {
  if (value instanceof Error) return value.stack ?? `${value.name}: ${value.message}`
  if (typeof value === "object" && value !== null) {
    const exception = value as { name?: unknown; message?: unknown; stack?: unknown }
    if (typeof exception.stack === "string") return exception.stack
    if (typeof exception.message === "string") {
      const name = typeof exception.name === "string" ? exception.name : "Error"
      return `${name}: ${exception.message}`
    }
  }
  return String(value)
}

function formatString(value: string): string {
  return needsQuoting(value) ? quoteLogfmt(value) : value
}

function quoteLogfmt(value: string): string {
  let result = '"'
  for (const character of value) {
    const codePoint = character.codePointAt(0)!
    switch (character) {
      case "\\": result += "\\\\"; break
      case '"': result += '\\"'; break
      case "\n": result += "\\n"; break
      case "\r": result += "\\r"; break
      case "\t": result += "\\t"; break
      case "\b": result += "\\b"; break
      case "\f": result += "\\f"; break
      case "\u000b": result += "\\v"; break
      case "\u0007": result += "\\a"; break
      default:
        if (codePoint < 0x20 || (codePoint >= 0x7f && codePoint <= 0x9f)) {
          result += `\\x${codePoint.toString(16).padStart(2, "0")}`
        } else {
          result += character
        }
    }
  }
  return `${result}"`
}

function needsQuoting(value: string): boolean {
  if (value.length === 0 || /[\s=\\"]/.test(value)) return true
  for (const character of value) {
    const codePoint = character.codePointAt(0)!
    if (codePoint < 0x20 || (codePoint >= 0x7f && codePoint <= 0x9f)) return true
  }
  return false
}

function isNotFound(error: unknown): boolean {
  return typeof error === "object" && error !== null && "code" in error && error.code === "ENOENT"
}

const noopLogger: RunnerLogger = {
  trace() {},
  debug() {},
  info() {},
  warn() {},
  error() {},
  fatal() {},
  child() { return noopLogger },
  flush: async () => {},
}

let activeLogger: RunnerLogger = noopLogger

function currentRunnerLogger(): RunnerLogger {
  return currentRunnerResources()?.logger ?? activeLogger
}

export function configureRunnerLogger(options: RunnerLoggerOptions = {}): RunnerLogger {
  activeLogger = createRunnerLogger(options)
  return activeLogger
}

export const runnerLogger: RunnerLogger = {
  trace(message, fields) { currentRunnerLogger().trace(message, fields) },
  debug(message, fields) { currentRunnerLogger().debug(message, fields) },
  info(message, fields) { currentRunnerLogger().info(message, fields) },
  warn(message, fields) { currentRunnerLogger().warn(message, fields) },
  error(message, fields) { currentRunnerLogger().error(message, fields) },
  fatal(message, fields) { currentRunnerLogger().fatal(message, fields) },
  child(component) {
    return {
      trace(message, fields) { currentRunnerLogger().child(component).trace(message, fields) },
      debug(message, fields) { currentRunnerLogger().child(component).debug(message, fields) },
      info(message, fields) { currentRunnerLogger().child(component).info(message, fields) },
      warn(message, fields) { currentRunnerLogger().child(component).warn(message, fields) },
      error(message, fields) { currentRunnerLogger().child(component).error(message, fields) },
      fatal(message, fields) { currentRunnerLogger().child(component).fatal(message, fields) },
      child(nestedComponent) { return runnerLogger.child(nestedComponent) },
      flush() { return currentRunnerLogger().flush() },
    }
  },
  flush() { return currentRunnerLogger().flush() },
}
