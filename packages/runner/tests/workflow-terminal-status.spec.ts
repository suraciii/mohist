import { describe, expect, it } from "vitest"
import { isTerminalWorkflowStatus, TERMINAL_WORKFLOW_STATUSES } from "../src/runtime/workflow-terminal-status.js"

// Wire-shape contract for the runner-side WorkflowRunStatus mapping.
// The server is the source of truth (see
// `packages/server/.../Workflow/Domain/Run/WorkflowRun.cs`); the runner
// only needs to recognize the canonical enum names and treat Completed /
// Stopped as terminal. Anything else must be treated as non-terminal so
// the convergence loop leaves the entry active for the next tick (or for
// a future push). Failed is intentionally non-terminal: it is a
// recoverable mid-state (Retry/Rerun/RerunFromStage revive it), so a
// failed run's workspace must be preserved for the next dispatch.

describe("isTerminalWorkflowStatus", () => {
  it("ReturnsTrueForCompleted", () => {
    expect(isTerminalWorkflowStatus("Completed")).toBe(true)
  })

  it("ReturnsTrueForStopped", () => {
    expect(isTerminalWorkflowStatus("Stopped")).toBe(true)
  })

  it("ReturnsFalseForFailed", () => {
    // Failed is a recoverable mid-state, NOT terminal: a run that can be
    // retried must not have its workspace reclaimed mid-retry.
    expect(isTerminalWorkflowStatus("Failed")).toBe(false)
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

  it("ReturnsFalseForCreated", () => {
    // New vocabulary value from the D1 state machine: built but not
    // started. Must be non-terminal — a freshly-built workflow still
    // owns its workspace.
    expect(isTerminalWorkflowStatus("Created")).toBe(false)
  })

  it("ReturnsFalseForAwaitingBinding", () => {
    expect(isTerminalWorkflowStatus("AwaitingBinding")).toBe(false)
  })

  it("ReturnsFalseForReady", () => {
    // New vocabulary value from the D1 state machine: assigned, waiting
    // for the bound runner to pick up work. Must be non-terminal — the
    // bound runner may still come back and pick the work up, so cleanup
    // cannot mark the workspace eligible.
    expect(isTerminalWorkflowStatus("Ready")).toBe(false)
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
    // Server sends the canonical enum name verbatim. Lowercase is a wire
    // contract violation; the runner
    // treats it as unknown / non-terminal.
    expect(isTerminalWorkflowStatus("completed")).toBe(false)
    expect(isTerminalWorkflowStatus("STOPPED")).toBe(false)
  })

  it("TerminalStatusesSetExactlyMatchesTheTwoTerminalStates", () => {
    expect(Array.from(TERMINAL_WORKFLOW_STATUSES).sort()).toEqual(["Completed", "Stopped"])
  })
})
