import { describe, expect, it, beforeEach, vi } from "vitest"
import type { WorkExecutor } from "../src/runtime/executor.js"
import type { ActionRegistry, ActionDefinition } from "../src/actions/registry.js"
import { defineAction } from "../src/actions/define-action.js"
import type { ServerConnection } from "../src/server/connection.js"
import type { ActionResult, JsonObject, DispatchWorkItem } from "../src/core/types.js"
import type { ActionHost } from "../src/actions/host.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

const mockFallbackWorkDir = "/tmp"

describe("Check verdict validation", () => {
  let executor: WorkExecutor
  let mockActionRegistry: ActionRegistry
    let capturedHandler: ((inputs: unknown, host: ActionHost) => Promise<ActionResult>) | null

  beforeEach(async () => {
    const mockWorkspaceManager = verifyOnlyWorkspaceManager({ path: "/tmp/test-work", branch: "main", changeDir: "/tmp/test-work" })

    const mockConnection = {} as unknown as ServerConnection

    capturedHandler = null
    const definition: ActionDefinition = defineAction({
      manifest: {
        name: "test/check-action",
        inputs: {
          path: { types: ["string"] },
          expect: { types: ["string"] },
        },
        outputs: [],
        errors: [{ code: "marker-failed" }],
      },
       run: async (inputs, host) => {
         if (capturedHandler) return await capturedHandler(inputs, host)
        return { output: null }
      },
    })
    mockActionRegistry = {
      resolve: vi.fn().mockImplementation(() => ({ kind: "definition", definition, canonicalName: definition.manifest.name })),
    } as unknown as ActionRegistry

    const mod = await import("../src/runtime/executor.js")
    executor = new mod.WorkExecutor(
      mockActionRegistry,
      mockWorkspaceManager as any,
      mockConnection,
      mockFallbackWorkDir,
    )
  })

  const mockAction = (result: ActionResult) => {
    capturedHandler = async () => result
  }

  const makeCheckWork = (checks: JsonObject[], variables: JsonObject = {}): DispatchWorkItem => ({
    workflowRunId: "wf-1",
    workId: "work-checks-1",
    workType: "checks",
    stage: "check",
    title: "Run checks",
    uses: "mohist/opencode",
    with: { checks },
    variables,
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
    mockAction({ output: { kind: "marker", found: true } })
    const work = makeCheckWork([{ name: "review-passed", uses: "core/marker", with: { path: "/tmp/test-work/review.md", expect: "<promise>PASS</promise>" } }])
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe("pass")
  })

  it("invalid check output is reported without serializing the rejected value", async () => {
    const output: Record<string, unknown> = {}
    output.self = output
    mockAction({ output } as unknown as ActionResult)

    const result = await executor.execute(makeCheckWork([{ name: "cyclic", uses: "test/action" }]), new AbortController().signal)

    expect(result).toMatchObject({ status: "fail", error: { code: "unexpected-error" } })
    expect(() => JSON.stringify(result)).not.toThrow()
    expect(result.output).toEqual([{ name: "cyclic", status: "fail", message: expect.any(String) }])
  })

  it("resolves a check working directory from the engine-owned input", async () => {
    let workDir = ""
    capturedHandler = async (_inputs, host) => {
      workDir = host.workDir
      return { output: null }
    }
    const work = makeCheckWork([{
      name: "subdirectory-check",
      uses: "core/marker",
      with: { "working-directory": "subdir", path: "review.md" },
    }])

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("pass")
    expect(workDir).toBe("/tmp/test-work/subdir")
  })

  it("renders a check declaration from its dispatch snapshot before validation", async () => {
    let capturedInputs: JsonObject | null = null
    capturedHandler = async (inputs) => {
      capturedInputs = inputs
      return { output: null }
    }
    const work = makeCheckWork([{
      name: "rendered-check",
      uses: "core/marker",
      with: {
        path: "${{ vars.reviewPath }}",
        expect: "${{ vars.marker }}",
      },
    }], {
      vars: { reviewPath: "review.md", marker: "<promise>PASS</promise>" },
    })

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("pass")
    expect(capturedInputs).toEqual({ path: "review.md", expect: "<promise>PASS</promise>" })
    expect((work.with!.checks as JsonObject[])[0]).toEqual({
      name: "rendered-check",
      uses: "core/marker",
      with: { path: "${{ vars.reviewPath }}", expect: "${{ vars.marker }}" },
    })
  })
})
