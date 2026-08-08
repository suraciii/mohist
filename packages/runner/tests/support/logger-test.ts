import { currentRunnerResources } from "../../src/system/filesystem.js"
import type { LogFields, LogLevel, RunnerLogger } from "../../src/system/logger.js"

export interface CapturedLog {
  readonly level: LogLevel
  readonly message: string
  readonly component: string
  readonly fields: LogFields
}

export interface LoggerCapture extends RunnerLogger {
  readonly records: CapturedLog[]
  readonly listeners: Set<(record: CapturedLog) => void>
}

export function capturedLogs(): readonly CapturedLog[] {
  return currentCapture().records
}

export function onCapturedLog(listener: (record: CapturedLog) => void): () => void {
  const capture = currentCapture()
  capture.listeners.add(listener)
  return () => capture.listeners.delete(listener)
}

interface LoggerCaptureState {
  readonly records: CapturedLog[]
  readonly listeners: Set<(record: CapturedLog) => void>
}

export function createLoggerCapture(component = "runner", state: LoggerCaptureState = {
  records: [],
  listeners: new Set(),
}): LoggerCapture {
  const { records, listeners } = state
  const emit = (level: LogLevel, message: string, fields?: LogFields) => {
    const record = { level, message, component, fields: { ...fields } }
    records.push(record)
    for (const listener of listeners) listener(record)
  }
  return {
    records,
    listeners,
    trace: (message, fields) => emit("TRACE", message, fields),
    debug: (message, fields) => emit("DEBUG", message, fields),
    info: (message, fields) => emit("INFO", message, fields),
    warn: (message, fields) => emit("WARN", message, fields),
    error: (message, fields) => emit("ERROR", message, fields),
    fatal: (message, fields) => emit("FATAL", message, fields),
    child: (childComponent) => createLoggerCapture(childComponent, state),
    flush: async () => {},
  }
}

function currentCapture(): LoggerCapture {
  const logger = currentRunnerResources()?.logger
  if (!logger || !isLoggerCapture(logger)) {
    throw new Error("runner logger capture context is not active")
  }
  return logger
}

function isLoggerCapture(logger: RunnerLogger): logger is LoggerCapture {
  return "records" in logger && Array.isArray(logger.records) && "listeners" in logger && logger.listeners instanceof Set
}
