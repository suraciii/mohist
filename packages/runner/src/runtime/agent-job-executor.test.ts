import { mkdtemp, readFile, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, describe, expect, it, vi } from "vitest"
import { AgentJobExecutor } from "./agent-job-executor.js"
import type { DispatchWorkItem } from "../core/types.js"

describe("AgentJobExecutor attachment delivery", () => {
  const workspaces: string[] = []

  afterEach(async () => {
    await Promise.all(workspaces.splice(0).map((path) => rm(path, { recursive: true, force: true })))
  })

  it("delivers an attachment-only input to the runtime as a readable workspace file", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-agent-job-attachment-"))
    workspaces.push(workDir)
    const runTurn = vi.fn(async (request: { prompt: string; fileParts?: readonly unknown[] }) => ({
      ok: true as const,
      value: {
        facts: {
          finalAssistantText: "received",
          runtimeSessionId: "runtime-1",
          workDir,
        },
        diagnostics: [],
      },
      diagnostics: [],
    }))
    const connection = {
      runnerId: "runner-1",
      getAgentSession: vi.fn(async () => ({ runtime: "opencode", runtimeSessionId: null, workDir })),
      openAgentInputAttachment: vi.fn(async () => ({
        bytes: new TextEncoder().encode("attachment contents"),
        contentType: "text/plain",
        contentDisposition: null,
      })),
      attachAgentSession: vi.fn(async () => null),
      agentSessionRuntimeEvents: vi.fn(async () => []),
    }
    const runtime = {
      ready: () => true,
      diagnostic: () => null,
      runTurn,
    }
    const work: DispatchWorkItem = {
      workflowRunId: "",
      workId: "work-1",
      workType: "agent-job",
      ownerKind: "agent-job",
      projectId: "project-1",
      agentJobId: "job-1",
      agentSessionId: "session-1",
      initialInputId: "input-1",
      initialTurnId: "turn-1",
      variables: { workspace: { path: workDir } },
      with: {
        attachments: [{ id: "attachment-1", name: "notes.txt", contentType: "text/plain", size: 19 }],
      },
    }

    const result = await new AgentJobExecutor(
      connection as never,
      { openCode: runtime as never, pi: null },
      null,
      workDir,
    ).execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(connection.openAgentInputAttachment).toHaveBeenCalledWith(
      "project-1",
      "session-1",
      "input-1",
      "attachment-1",
      expect.any(AbortSignal),
    )
    expect(runTurn).toHaveBeenCalledOnce()
    const request = runTurn.mock.calls[0]?.[0]
    expect(request.prompt).toContain("[mohist-attachments]")
    expect(request.prompt).toContain("notes.txt")
    expect(request.prompt).not.toContain("Please read")
    expect(request.fileParts).toBeUndefined()
    expect(await readFile(join(workDir, ".mohist/attachments/input-1/attachment-1/notes.txt"), "utf8"))
      .toBe("attachment contents")
  })

  it("passes delivered images to the OpenCode runtime as native file parts", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-agent-job-image-"))
    workspaces.push(workDir)
    const runTurn = vi.fn(async (request: { prompt: string; fileParts?: readonly unknown[] }) => ({
      ok: true as const,
      value: {
        facts: {
          finalAssistantText: "received",
          runtimeSessionId: "runtime-1",
          workDir,
        },
        diagnostics: [],
      },
      diagnostics: [],
    }))
    const connection = {
      runnerId: "runner-1",
      getAgentSession: vi.fn(async () => ({ runtime: "opencode", runtimeSessionId: null, workDir })),
      openAgentInputAttachment: vi.fn(async () => ({
        bytes: new Uint8Array([1, 2, 3]),
        contentType: "image/png",
        contentDisposition: null,
      })),
      attachAgentSession: vi.fn(async () => null),
      agentSessionRuntimeEvents: vi.fn(async () => []),
    }
    const runtime = {
      ready: () => true,
      diagnostic: () => null,
      runTurn,
    }
    const work: DispatchWorkItem = {
      workflowRunId: "",
      workId: "work-1",
      workType: "agent-job",
      ownerKind: "agent-job",
      projectId: "project-1",
      agentJobId: "job-1",
      agentSessionId: "session-1",
      initialInputId: "input-1",
      initialTurnId: "turn-1",
      variables: { workspace: { path: workDir } },
      with: {
        prompt: "inspect the image",
        attachments: [{ id: "attachment-1", name: "diagram.png", contentType: "image/png", size: 3 }],
      },
    }

    const result = await new AgentJobExecutor(
      connection as never,
      { openCode: runtime as never, pi: null },
      null,
      workDir,
    ).execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(runTurn.mock.calls[0]?.[0].fileParts).toEqual([{
      mime: "image/png",
      filename: "diagram.png",
      url: "data:image/png;base64,AQID",
    }])
  })
})
