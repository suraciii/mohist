import { afterEach, beforeEach, vi } from "vitest"
import { UnexpectedConsoleRecorder } from "./support/unexpected-console.js"

const unexpectedConsole = new UnexpectedConsoleRecorder()

beforeEach(() => {
  unexpectedConsole.clear()
  vi.spyOn(console, "error").mockImplementation((...values) => {
    unexpectedConsole.record("error", values)
  })
  vi.spyOn(console, "warn").mockImplementation((...values) => {
    unexpectedConsole.record("warn", values)
  })
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllEnvs()

  const error = unexpectedConsole.takeError()
  if (error) throw error
})
