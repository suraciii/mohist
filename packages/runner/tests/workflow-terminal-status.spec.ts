import { describe, expect, it } from "vitest"
import { isTerminalWorkflowStatus, TERMINAL_WORKFLOW_STATUSES } from "../src/runtime/workflow-terminal-status.js"

// Wire-shape contract for the runner-side WorkflowRunStatus mapping.
// The server is the source of truth (see
// `packages/server/.../Workflow/Domain/Run/WorkflowRun.cs`); the runner
// only needs to recognize the canonical enum names and treat Completed /
// Stopped / Failed as terminal. Anything else must be treated as
// non-terminal so the convergence loop leaves the entry active for the
// next tick (or for a future push).

describe("isTerminalWorkflowStatus", () => {
  it("ReturnsTrueForCompleted", () => {
    expect(isTerminalWorkflowStatus("Completed")).toBe(true)
  })

  it("ReturnsTrueForStopped", () => {
    expect(isTerminalWorkflowStatus("Stopped")).toBe(true)
  })

  it("ReturnsTrueForFailed", () => {
    expect(isTerminalWorkflowStatus("Failed")).toBe(true)
  })

  it("ReturnsFalseForRunning", () => {
    expect(isTerminalWorkflowStatus("Running")).toBe(false)
  })

  it("ReturnsFalseForPaused", () => {
    expect(isTerminalWorkflowStatus("Paused")).toBe(false)
  })

  it("ReturnsFalseForAwaitingApproval", () => {
    expect(isTerminalWorkflowStatus("AwaitingApproval")).toBe(false)
  })

  it("ReturnsFalseForPending", () => {
    expect(isTerminalWorkflowStatus("Pending")).toBe(false)
  })

  it("ReturnsFalseForNull", () => {
    expect(isTerminalWorkflowStatus(null)).toBe(false)
  })

  it("ReturnsFalseForUndefined", () => {
    expect(isTerminalWorkflowStatus(undefined)).toBe(false)
  })

  it("ReturnsFalseForEmptyString", () => {
    expect(isTerminalWorkflowStatus("")).toBe(false)
  })

  it("ReturnsFalseForUnknownStatus", () => {
    // A status the runner does not recognize must NOT be treated as
    // terminal: better to leave an entry active for a future tick than
    // to mark it eligible by mistake.
    expect(isTerminalWorkflowStatus("SomeUnknownState")).toBe(false)
  })

  it("IsCaseSensitiveLowercaseNotRecognized", () => {
    // Server sends the canonical enum name verbatim (per T-001
    // contract). Lowercase is a wire contract violation; the runner
    // treats it as unknown / non-terminal.
    expect(isTerminalWorkflowStatus("completed")).toBe(false)
    expect(isTerminalWorkflowStatus("STOPPED")).toBe(false)
  })

  it("TerminalStatusesSetExactlyMatchesTheThreeTerminalStates", () => {
    expect(Array.from(TERMINAL_WORKFLOW_STATUSES).sort()).toEqual(["Completed", "Failed", "Stopped"])
  })
})