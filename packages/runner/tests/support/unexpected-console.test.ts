import { describe, expect, it, vi } from "vitest"
import { UnexpectedConsoleRecorder } from "./unexpected-console.js"

describe("UnexpectedConsoleRecorder", () => {
  it("EmptyRecorder_DoesNotReportError", () => {
    const recorder = new UnexpectedConsoleRecorder()

    expect(recorder.takeError()).toBeNull()
  })

  it("RecordedError_ReportsError", () => {
    const recorder = new UnexpectedConsoleRecorder()

    recorder.record("error", [new Error("connection lost")])

    expect(recorder.takeError()).toMatchObject({
      message: "Unexpected console output:\n  - error: Error: connection lost",
    })
  })

  it("RecordedWarning_ReportsWarning", () => {
    const recorder = new UnexpectedConsoleRecorder()

    recorder.record("warn", ["retrying", { attempt: 2 }])

    expect(recorder.takeError()).toMatchObject({
      message: "Unexpected console output:\n  - warn: retrying {\"attempt\":2}",
    })
  })

  it("LocallyCapturedWarning_DoesNotReachGlobalRecorder", () => {
    const recorder = new UnexpectedConsoleRecorder()
    const target = {
      warn: (...values: unknown[]) => recorder.record("warn", values),
    }
    const warningSpy = vi.spyOn(target, "warn").mockImplementation(() => undefined)

    try {
      target.warn("expected warning")

      expect(warningSpy).toHaveBeenCalledOnce()
      expect(warningSpy).toHaveBeenCalledWith("expected warning")
      expect(recorder.takeError()).toBeNull()
    } finally {
      warningSpy.mockRestore()
    }
  })

  it("RepeatedCalls_AreGroupedAndClearedAfterReporting", () => {
    const recorder = new UnexpectedConsoleRecorder()

    recorder.record("warn", ["retrying"])
    recorder.record("warn", ["retrying"])

    expect(recorder.takeError()).toMatchObject({
      message: "Unexpected console output:\n  - warn: retrying (2x)",
    })
    expect(recorder.takeError()).toBeNull()
  })
})
