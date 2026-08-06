import { afterEach, beforeEach, vi } from "vitest"
import { UnexpectedConsoleRecorder } from "./support/unexpected-console.js"
import { cleanupRegisteredTempDirs } from "./support/temp-dir.js"
import { installLoggerCapture } from "./support/logger-test.js"

const unexpectedConsole = new UnexpectedConsoleRecorder()
let restoreLogger: (() => void) | null = null

beforeEach(() => {
  restoreLogger = installLoggerCapture()
  unexpectedConsole.clear()
  vi.spyOn(console, "error").mockImplementation((...values) => {
    unexpectedConsole.record("error", values)
  })
  vi.spyOn(console, "warn").mockImplementation((...values) => {
    unexpectedConsole.record("warn", values)
  })
})

afterEach(async () => {
  restoreLogger?.()
  restoreLogger = null
  vi.useRealTimers()
  vi.unstubAllEnvs()

  const consoleError = unexpectedConsole.takeError()
  let cleanupError: unknown
  let cleanupFailed = false
  try {
    await cleanupRegisteredTempDirs()
  } catch (error) {
    cleanupError = error
    cleanupFailed = true
  }

  if (consoleError && cleanupFailed) {
    throw new AggregateError([consoleError, cleanupError], "Test emitted unexpected console output and leaked temp cleanup")
  }
  if (cleanupFailed) throw cleanupError
  if (consoleError) throw consoleError
})
