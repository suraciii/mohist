import {
  setRunnerLoggerForTest,
  type LogFields,
  type LogLevel,
  type RunnerLogger,
} from "../../src/system/logger.js"

export interface CapturedLog {
  readonly level: LogLevel
  readonly message: string
  readonly component: string
  readonly fields: LogFields
}

const records: CapturedLog[] = []
const listeners = new Set<(record: CapturedLog) => void>()

export function installLoggerCapture(): () => void {
  records.length = 0
  listeners.clear()
  return setRunnerLoggerForTest(createCaptureLogger("runner"))
}

export function capturedLogs(): readonly CapturedLog[] {
  return records
}

export function onCapturedLog(listener: (record: CapturedLog) => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

function createCaptureLogger(component: string): RunnerLogger {
  const emit = (level: LogLevel, message: string, fields?: LogFields) => {
    const record = { level, message, component, fields: { ...fields } }
    records.push(record)
    for (const listener of listeners) listener(record)
  }
  return {
    trace: (message, fields) => emit("TRACE", message, fields),
    debug: (message, fields) => emit("DEBUG", message, fields),
    info: (message, fields) => emit("INFO", message, fields),
    warn: (message, fields) => emit("WARN", message, fields),
    error: (message, fields) => emit("ERROR", message, fields),
    fatal: (message, fields) => emit("FATAL", message, fields),
    child: createCaptureLogger,
    flush: async () => {},
  }
}
