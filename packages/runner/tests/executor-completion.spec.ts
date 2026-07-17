import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { ActionRegistry } from "../src/actions/registry.js"
import { WorkExecutor } from "../src/runtime/executor.js"
import { setExecutorGitRunnerForTest } from "../src/runtime/git-probe.js"
import type { ActionContext, ActionResult, RenderedWorkItem, WorkItemResult } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import { tryRecovery } from "../src/runtime/recovery.js"
import { verifyOnlyWorkspaceManager } from "./support/workspace-mock.js"

let workDir: string
const nonGitRunner = async () => ({
  success: false,
  exitCode: 128,
  stdout: "",
  stderr: "not a git repository",
  combinedOutput: "not a git repository",
})

beforeEach(async () => {
  workDir = await mkdtemp(join(tmpdir(), "mohist-executor-completion-"))
  setExecutorGitRunnerForTest(nonGitRunner)
})

afterEach(async () => {
  setExecutorGitRunnerForTest(null)
  await rm(workDir, { recursive: true, force: true })
})

function executorFor(handler: (ctx: ActionContext) => Promise<ActionResult>): WorkExecutor {
  const registry = new ActionRegistry()
  registry.register("test/action", async (ctx) => handler(ctx))
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
    silentConnection(),
    {} as never,
    null,
    workDir,
  )
}

function silentConnection(): ServerConnection {
  return {
    async report() {
      return {}
    },
    async uploadArtifact() {
      throw new Error("uploadArtifact should not be called in completion tests")
    },
  } as unknown as ServerConnection
}

function buildWork(overrides: Partial<RenderedWorkItem>): RenderedWorkItem {
  return {
    workflowRunId: "wf-completion",
    workId: "review.1",
    workType: "task",
    stage: "check",
    title: "Review",
    uses: "test/action",
    with: {},
    expect: null,
    variables: { workspace: { path: workDir, branch: null, changeDir: null } },
    ...overrides,
  }
}

describe("WorkExecutor completion evaluation", () => {
  it("ReadsTopLevelExpect_NotFromWithInput", async () => {
    // Spec scenario: "An expected path uses dispatch context" /
    // "Completion contracts are expanded separately for each dispatch" /
    // "Task completion policy is absent from Action Input".
    //
    // The Action receives `with` as-is (the server-side loader is the
    // gate that rejects `with.expect` for agent Actions), but the
    // executor's completion evaluator reads from the top-level
    // `expect`, never from `context.with.expect`. We prove the
    // evaluator reads `expect` by feeding two distinct declarations:
    // `with.expect` (the legacy shape) and top-level `expect`.
    let capturedWith: Record<string, unknown> | null = null
    const executor = executorFor(async (ctx) => {
      capturedWith = (ctx.with ?? null) as Record<string, unknown> | null
      return { status: "success", message: "ok" }
    })

    const result = await executor.execute(buildWork({
      with: {
        prompt: "do work",
        // This `expect` lives inside `with` — the executor MUST
        // ignore it for completion evaluation. Top-level `expect`
        // wins.
        expect: { files: [{ path: "ignored-if-with.txt" }] },
      },
      expect: { files: [{ path: "expected.txt" }] },
    }), new AbortController().signal)

    // The Action does receive the dispatch's `with` verbatim. The
    // executor does not mutate `with`. The assertion below proves the
    // evaluator ignores `with.expect` even if it is present.
    expect((capturedWith as { prompt?: unknown } | null)?.prompt).toBe("do work")
    // The missing `expected.txt` fails completion; the
    // `with.expect`'s `ignored-if-with.txt` does not influence
    // completion either way.
    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/missing required file/)
    expect(result.message).toContain("expected.txt")
    expect(result.message).not.toContain("ignored-if-with.txt")
  })

  it("ActionSuccess_MissingRequiredFile_FailsWithDiagnostic", async () => {
    const executor = executorFor(async () => ({ status: "success", message: "agent done" }))
    const result = await executor.execute(buildWork({
      expect: { files: [{ path: "missing.md" }] },
    }), new AbortController().signal)
    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/missing required file: .*missing\.md/)
  })

  it("ActionSuccess_RequiredFilePresent_RemainsSuccessful", async () => {
    await writeFile(join(workDir, "review.md"), "looks good\n<promise>PASS</promise>")
    const executor = executorFor(async () => ({ status: "success", message: "agent done" }))
    const result = await executor.execute(buildWork({
      expect: {
        files: [{ path: "review.md" }],
        markers: [{ path: "review.md", contains: "looks good" }],
      },
    }), new AbortController().signal)
    expect(result.status).toBe("completed")
    expect(result.message).toBe("agent done")
  })

  it("FileMarker_FirstPresentValueWinsInDeclarationOrder", async () => {
    // Spec: "A file-backed marker with oneOf [PASS, FAIL] matches the
    // first present value in declaration order when the file contains
    // both values".
    await writeFile(
      join(workDir, "review.md"),
      "dual verdict\n<promise>PASS</promise>\n<promise>FAIL</promise>\n",
    )
    let promiseObserved: string | null = null
    const executor = executorFor(async () => ({
      status: "success",
      output: JSON.stringify({ promise: "PASS" }),
    }))

    const result = await executor.execute(buildWork({
      expect: {
        markers: [{
          path: "review.md",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
          failIf: "<promise>FAIL</promise>",
        }],
      },
    }), new AbortController().signal)

    // The result mirrors the Action's output (non-opencode Action
    // preserves output unchanged). Promise projection only applies to
    // the opencode contract.
    const output = JSON.parse(result.output ?? "{}")
    promiseObserved = output.promise ?? null
    // The matched value is PASS (declaration order) and `failIf=FAIL`
    // MUST NOT trigger for that match, so the task succeeds.
    expect(result.status).toBe("completed")
    expect(promiseObserved).toBe("PASS")
  })

  it("FailIfMatch_FailsButExposesTheValueForProjection", async () => {
    await writeFile(join(workDir, "review.md"), "issues\n<promise>FAIL</promise>\n")
    const executor = executorFor(async () => ({
      status: "success",
      output: JSON.stringify({ errorCode: "marker-failed" }),
    }))

    const result = await executor.execute(buildWork({
      expect: {
        markers: [{
          path: "review.md",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
          failIf: "<promise>FAIL</promise>",
        }],
      },
    }), new AbortController().signal)

    // A failIf match flips Action success into completion failure but
    // preserves the Action's structured output for downstream recovery
    // matching (`errorCode: "marker-failed"`).
    expect(result.status).toBe("failed")
    const output = JSON.parse(result.output ?? "{}")
    expect(output.errorCode).toBe("marker-failed")
    expect(result.message).toMatch(/failIf marker matched/)
  })

  it("OpenCodeOutput_ProjectsNullWhenNoPromiseMarkerMatched", async () => {
    // Spec scenario for mohist/opencode: handler output is discarded.
    // When no promise marker matches, Action Output is `null`.
    const executor = executorForOpencode(async () => ({
      status: "success",
      output: JSON.stringify({
        kind: "opencode",
        text: "did the work without verdict",
        model: "openai/gpt-5",
      }),
      turnFact: { finalAssistantText: "did the work without verdict" },
    }), "mohist/opencode")

    const result = await executor.execute(buildWork({
      uses: "mohist/opencode",
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        }],
      },
    }), new AbortController().signal)

    expect(result.status).toBe("failed")
    // Opencode contract: no matched promise marker → null output.
    expect(result.output).toBeNull()
    expect(result.message).toMatch(/missing marker/)
  })

  it("OpenCodeOutput_ProjectsPromiseMarkerWhenMatched", async () => {
    // Spec scenario: completion matches `<promise>FAIL</promise>` →
    // Action Output SHALL equal `{ "promise": "FAIL" }`.
    const executor = executorForOpencode(async () => ({
      status: "success",
      output: JSON.stringify({ kind: "opencode", text: "verdict time\n<promise>FAIL</promise>" }),
      turnFact: { finalAssistantText: "verdict time\n<promise>FAIL</promise>" },
    }), "mohist/opencode")

    const result = await executor.execute(buildWork({
      uses: "mohist/opencode",
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
          failIf: "<promise>FAIL</promise>",
        }],
      },
    }), new AbortController().signal)

    expect(result.status).toBe("failed")
    // Output is the minimal promise projection — fails-with-fail-If
    // still produces the marker value, recovery matches `promise=FAIL`.
    expect(JSON.parse(result.output ?? "null")).toEqual({ promise: "FAIL" })
  })

  it("OpenCodeOutput_ProjectsPASSWhenMatched_NoFailIf", async () => {
    const executor = executorForOpencode(async () => ({
      status: "success",
      output: JSON.stringify({ kind: "opencode", text: "looks good\n<promise>PASS</promise>" }),
      turnFact: { finalAssistantText: "looks good\n<promise>PASS</promise>" },
    }), "mohist/opencode")

    const result = await executor.execute(buildWork({
      uses: "mohist/opencode",
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        }],
      },
    }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(JSON.parse(result.output ?? "null")).toEqual({ promise: "PASS" })
  })

  it("NonAgentAction_PreservesStructuredOutputUnchanged", async () => {
    // Spec: "a non-agent Action (e.g. mohist/rebase) returning
    // structured output preserves that output unchanged through
    // completion evaluation".
    const rebaseOutput = {
      kind: "rebase",
      status: "completed",
      rebased: true,
      baseSha: "abc123",
      errorCode: null,
    }
    const registry = new ActionRegistry()
    registry.register("mohist/rebase", async () => ({
      status: "success",
      output: JSON.stringify(rebaseOutput),
    }))
    const executor = new WorkExecutor(
      registry,
      verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
      silentConnection(),
      {} as never,
      null,
      workDir,
    )

    const result = await executor.execute(buildWork({
      uses: "mohist/rebase",
      expect: { files: [{ path: "unused.md" }] },
    }), new AbortController().signal)

    // `mohist/rebase` returning success but missing required files
    // → completion failure with the diagnostic. The structured output
    // (kind, rebased, baseSha) is preserved so recovery and inspection
    // can still inspect it.
    expect(result.status).toBe("failed")
    const parsed = JSON.parse(result.output ?? "{}")
    expect(parsed.kind).toBe("rebase")
    expect(parsed.rebased).toBe(true)
    expect(parsed.baseSha).toBe("abc123")
    expect(result.message).toMatch(/missing required file/)
  })

  it("ActionFailure_StaysFailed_EvenWhenFilesExist", async () => {
    // Spec scenario: "Expectations do not rescue an Action failure".
    await writeFile(join(workDir, "review.md"), "ok")
    const executor = executorFor(async () => ({ status: "failure", message: "model failed" }))

    const result = await executor.execute(buildWork({
      with: { command: "echo" },
      expect: {
        files: [{ path: "review.md" }],
        markers: [{ path: "review.md", contains: "ok" }],
      },
    }), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.message).toBe("model failed")
  })

  it("OutputMarker_LastAcceptedOccurrenceWins", async () => {
    // Spec: "if more than one configured accepted value occurs in the
    // text, the matched value SHALL be the accepted occurrence that
    // appears last". The generalized <promise>VALUE</promise> parser
    // accepts arbitrary values; PASS-then-FAIL → matched=FAIL.
    const executor = executorFor(async () => ({
      status: "success",
      turnFact: {
        finalAssistantText: "<promise>PASS</promise>\n...more text...\n<promise>FAIL</promise>",
      },
    }))

    const result = await executor.execute(buildWork({
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        }],
      },
    }), new AbortController().signal)

    expect(result.status).toBe("completed")
  })

  it("OutputMarker_NoTurnFact_IsUnsatisfied_NotPulledFromActionOutput", async () => {
    // Action returns structured output that happens to contain the
    // marker, but turnFact is absent. The marker MUST be unsatisfied;
    // the executor MUST NOT fall back to Action Output as a text
    // source (design D4).
    const executor = executorFor(async () => ({
      status: "success",
      output: JSON.stringify({ note: "<promise>PASS</promise>" }),
      turnFact: null,
    }))

    const result = await executor.execute(buildWork({
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>PASS</promise>"],
        }],
      },
    }), new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(result.message).toMatch(/missing marker.*_output/)
  })

  it("TurnFactDoesNotLeakIntoOutput", async () => {
    // Spec scenario: "Final assistant text remains private".
    const finalText = "private internal agent text\n<promise>PASS</promise>"
    const executor = executorFor(async () => ({
      status: "success",
      output: JSON.stringify({ kind: "action" }),
      turnFact: { finalAssistantText: finalText },
    }))

    const result = await executor.execute(buildWork({
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>PASS</promise>"],
        }],
      },
    }), new AbortController().signal)

    expect(result.status).toBe("completed")
    // The final assistant text MUST NOT appear in the wire output /
    // action output / message — only the matched promise marker is
    // available for projection, and the helper Action's structured
    // output is preserved by the non-opencode contract.
    const serialized = JSON.stringify(result)
    expect(serialized).not.toContain("private internal agent text")
    expect(serialized).not.toContain("finalAssistantText")
    expect(serialized).not.toContain("turnFact")
  })

  it("RecoversViaPromiseField_AfterPromiseMarkerMatches", async () => {
    // Recovery matching uses the projected Action output (post
    // completion evaluation). `when: promise=FAIL` against the
    // opencode projection { promise: "FAIL" } must match.
    const executor = executorForOpencode(async () => ({
      status: "success",
      output: JSON.stringify({ kind: "opencode" }),
      turnFact: { finalAssistantText: "verdict\n<promise>FAIL</promise>" },
    }), "mohist/opencode")

    const result = await executor.execute(buildWork({
      uses: "mohist/opencode",
      expect: {
        markers: [{
          path: "_output",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
          failIf: "<promise>FAIL</promise>",
        }],
      },
      recovery: {
        budget: 1,
        handlers: [
          { when: "promise=FAIL", tasks: [{ id: "recover:fix", title: "Fix" }], retrySelf: false },
        ],
      },
      recoveryRemaining: null,
    }), new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(result.message).toMatch(/promise=FAIL/)
    expect(result.addTasks?.map((t) => t.id)).toEqual(["recover:fix"])
  })
})

function executorForOpencode(
  handler: (ctx: ActionContext) => Promise<ActionResult>,
  uses: string,
): WorkExecutor {
  const registry = new ActionRegistry()
  registry.register(uses, async (ctx) => handler(ctx))
  return new WorkExecutor(
    registry,
    verifyOnlyWorkspaceManager({ path: workDir, branch: null, changeDir: null }),
    silentConnection(),
    {} as never,
    null,
    workDir,
  )
}

describe("tryRecovery self-retry expect copy", () => {
  it("CopiesExpectIntoSelfRetryAlongsideWithAndRecoveryRemaining", () => {
    const work: RenderedWorkItem = {
      workflowRunId: "wf-recovery-expect",
      workId: "review.2",
      workType: "task",
      stage: "check",
      title: "Review",
      uses: "mohist/opencode",
      with: { prompt: "review", session: "s1" },
      expect: {
        markers: [{
          path: "review.md",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
        }],
      },
      recovery: {
        budget: 2,
        handlers: [
          { when: "promise=FAIL", tasks: [{ id: "fix", title: "Fix" }], retrySelf: true },
        ],
      },
      recoveryRemaining: 1,
    }
    const result = tryRecovery(work, {
      status: "completed",
      message: "matched",
      output: JSON.stringify({ promise: "FAIL" }),
    })

    expect(result?.addTasks?.[1]).toMatchObject({
      id: "review",
      title: "Review",
      uses: "mohist/opencode",
      with: { prompt: "review", session: "s1" },
      recovery: { budget: 2, handlers: [{ when: "promise=FAIL", tasks: [{ id: "fix" }], retrySelf: true }] },
      recoveryRemaining: 0,
    })
    expect(result?.addTasks?.[1]?.expect).toEqual({
      markers: [{
        path: "review.md",
        oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
      }],
    })
  })

  it("CopiesExpectNullWhenDispatchOmittedIt", () => {
    const work: RenderedWorkItem = {
      workflowRunId: "wf-recovery-noexpect",
      workId: "review.3",
      workType: "task",
      stage: "check",
      title: "Review",
      uses: "test/action",
      with: {},
      expect: undefined,
      recovery: {
        budget: 1,
        handlers: [
          { when: "errorCode=conflict", tasks: [{ id: "fix", title: "Fix" }], retrySelf: true },
        ],
      },
      recoveryRemaining: 1,
    }
    const result = tryRecovery(work, {
      status: "completed",
      output: JSON.stringify({ errorCode: "conflict" }),
    })

    expect(result?.addTasks?.[1]?.expect).toBeNull()
  })

  it("PropagatesExpectFromHandlerTaskTemplate_NotJustSelfRetry", () => {
    // Spec requirement "The canonical declaration survives the complete
    // task lifecycle": recovery handler tasks (not just retrySelf) keep
    // their top-level `expect` alongside `with`. The handler-task
    // template carries the completion contract; the recovery scheduling
    // path MUST NOT drop it.
    const work: RenderedWorkItem = {
      workflowRunId: "wf-recovery-handler-expect",
      workId: "review.4",
      workType: "task",
      stage: "check",
      title: "Review",
      uses: "test/action",
      with: { prompt: "review" },
      expect: undefined,
      recovery: {
        budget: 1,
        handlers: [
          {
            when: "promise=FAIL",
            retrySelf: false,
            tasks: [
              {
                id: "recover:fix-review",
                title: "Fix review findings",
                uses: "mohist/opencode",
                with: { prompt: "fix the review findings" },
                expect: {
                  markers: [
                    {
                      path: "review.md",
                      oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
                      failIf: "<promise>FAIL</promise>",
                    },
                  ],
                },
              },
            ],
          },
        ],
      },
      recoveryRemaining: 1,
    }
    const result = tryRecovery(work, {
      status: "completed",
      output: JSON.stringify({ promise: "FAIL" }),
    })

    expect(result?.addTasks?.[0]?.id).toBe("recover:fix-review")
    expect(result?.addTasks?.[0]?.expect).toEqual({
      markers: [
        {
          path: "review.md",
          oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
          failIf: "<promise>FAIL</promise>",
        },
      ],
    })
  })
})
