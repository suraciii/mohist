import { describe, expect, it, beforeEach, vi } from "vitest"
import type { WorkExecutor } from "../src/runtime/executor.js"
import type { ActionRegistry } from "../src/actions/registry.js"
import type { ServerConnection } from "../src/server/connection.js"
import type { AcpSessionManager, SharedAcpConnection } from "../src/runtime/acp-connection.js"
import type { ActionResult, JsonObject, RenderedWorkItem } from "../src/core/types.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

const mockFallbackWorkDir = "/tmp"

describe("Check verdict validation", () => {
  let executor: WorkExecutor
  let mockActionRegistry: ActionRegistry

  beforeEach(async () => {
    const mockWorkspaceManager = verifyOnlyWorkspaceManager({ path: "/tmp/test-work", branch: "main", changeDir: "/tmp/test-work" })

    const mockConnection = {} as unknown as ServerConnection
    const mockSessionManager = {} as unknown as AcpSessionManager
    const mockAcpConnection: SharedAcpConnection | null = null

    mockActionRegistry = {
      resolve: vi.fn(),
      register: vi.fn(),
    } as unknown as ActionRegistry

    const mod = await import("../src/runtime/executor.js")
    executor = new mod.WorkExecutor(
      mockActionRegistry,
      mockWorkspaceManager as any,
      mockConnection,
      mockSessionManager,
      mockAcpConnection,
      mockFallbackWorkDir,
    )
  })

  const mockAction = (result: ActionResult) => {
    ;(mockActionRegistry.resolve as any).mockImplementation(() => async () => result)
  }

  const makeCheckWork = (checks: JsonObject[]): RenderedWorkItem => ({
    workflowRunId: "wf-1",
    workId: "work-checks-1",
    workType: "checks",
    stage: "check",
    title: "Run checks",
    uses: "mohist/acp-agent",
    with: { checks },
    variables: {},
    projectId: "project-1",
    issueNumber: 1,
  })

  it("MissingPASSVerdictMarker_ReportsCheckVerdictFailure", async () => {
    mockAction({ error: { code: "marker-failed", message: "Marker missing in /tmp/test-work/review.md" } })
    const work = makeCheckWork([{ name: "review-passed", uses: "core/marker", with: { path: "/tmp/test-work/review.md", expect: "<promise>PASS</promise>" } }])
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("fail")
    expect(result.message).toContain("review-passed")
    expect(result.message).toContain("expected verdict marker")
    expect(result.message).not.toContain("ai-review")
  })

  it("VerdictMarkerFAILInReviewMd_ReportedAsCheckVerdictFailure_NotTaskArtifact", async () => {
    mockAction({ error: { code: "marker-failed", message: "Marker missing in /tmp/test-work/review.md" } })
    const work = makeCheckWork([{ name: "review-passed", uses: "core/marker", with: { path: "/tmp/test-work/review.md", expect: "<promise>PASS</promise>" } }])
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("fail")
    expect(result.message).toContain("review-passed")
    expect(result.message).toContain("expected verdict marker '<promise>PASS</promise>'")
    expect(result.message).not.toContain("ai-review")
    expect(result.message).not.toContain("task artifact")
  })

  it("AllChecksPass_ReturnsPassStatus", async () => {
    mockAction({ output: "Marker found in /tmp/test-work/review.md" })
    const work = makeCheckWork([{ name: "review-passed", uses: "core/marker", with: { path: "/tmp/test-work/review.md", expect: "<promise>PASS</promise>" } }])
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("pass")
  })
})
