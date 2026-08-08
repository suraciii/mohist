import { describe, expect, it as vitestIt, vi } from "vitest"
import {
  attachmentWorkspacePath,
  buildManifestBlock,
  deliverAcceptedAttachments,
  type AttachmentDescriptor,
} from "./attachment-delivery.js"
import { MemoryFileSystem } from "../../tests/support/memory-filesystem.js"
import { withTestRunnerResources } from "../../tests/support/test-resources.js"

const signal = new AbortController().signal

function it(name: string, body: (fileSystem: MemoryFileSystem) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const fileSystem = new MemoryFileSystem()
    try {
      await withTestRunnerResources(async () => await body(fileSystem), { fileSystem })
    } finally {
      await fileSystem.deleteDirectory("/")
      if (fileSystem.exists("/")) throw new Error("attachment test filesystem was not cleaned up")
    }
  })
}

describe("attachment delivery", () => {
  it("materializes content and adds native image data without exposing caller fields", async (fileSystem) => {
    const workDir = "/virtual/attachment-delivery"
    const descriptor: AttachmentDescriptor = {
      id: "attachment-1",
      name: "diagram.png",
      contentType: "image/png",
      size: 3,
    }
    const openAgentInputAttachment = vi.fn(async () => ({
      bytes: new Uint8Array([1, 2, 3]),
      contentType: "image/png",
      contentDisposition: null,
    }))

    const result = await deliverAcceptedAttachments({
      projectId: "project-1",
      agentSessionId: "session-1",
      inputId: "input-1",
      workDir,
      connection: { openAgentInputAttachment } as never,
      signal,
    }, [descriptor])

    const delivered = result.attachments[0]
    expect(delivered?.status).toBe("delivered")
    if (delivered?.status !== "delivered") throw new Error("attachment was not delivered")
    expect(await fileSystem.readBinary(delivered.workspacePath)).toEqual(new Uint8Array([1, 2, 3]))
    expect(delivered.filePart).toEqual({
      mime: "image/png",
      filename: "diagram.png",
      url: "data:image/png;base64,AQID",
    })
    expect(openAgentInputAttachment).toHaveBeenCalledWith(
      "project-1",
      "session-1",
      "input-1",
      "attachment-1",
      signal,
    )
    expect(result.manifestBlock).toContain(".mohist/attachments/input-1/attachment-1/diagram.png")
    expect(result.manifestBlock).not.toContain("temp-url")
    expect(result.manifestBlock).not.toContain("provider-token")
    expect(result.manifestBlock).not.toContain("raw-event")
  })

  it("reports a delivery-time fetch failure as unavailable", async () => {
    const descriptor: AttachmentDescriptor = {
      id: "attachment-2",
      name: "notes.txt",
      contentType: "text/plain",
      size: 10,
    }
    const result = await deliverAcceptedAttachments({
      projectId: "project-1",
      agentSessionId: "session-1",
      inputId: "input-1",
      workDir: "/workspace",
      connection: {
        openAgentInputAttachment: vi.fn(async () => {
          throw new Error("storage unavailable")
        }),
      } as never,
      signal,
    }, [descriptor])

    expect(result.attachments).toEqual([{
      descriptor,
      status: "unavailable",
      reason: "attachment fetch failed: storage unavailable",
    }])
    expect(result.manifestBlock).toContain("notes.txt (text/plain, 10 bytes) unavailable")
    expect(result.manifestBlock).not.toContain("available at")
  })

  it("uses an attributed manifest as the initiating content for an attachment-only turn", () => {
    const manifest = buildManifestBlock([{
      descriptor: {
        id: "attachment-3",
        name: "report.pdf",
        contentType: "application/pdf",
        size: 12,
      },
      status: "delivered",
      workspacePath: "/workspace/.mohist/attachments/input-1/attachment-3/report.pdf",
      filePart: null,
    }])

    expect(manifest.startsWith("[mohist-attachments]")).toBe(true)
    expect(manifest).toContain("Mohist system metadata")
    expect(manifest).not.toContain("Please")
    expect(manifest).toContain("not a user instruction")
  })

  it("keeps attachment paths inside the input directory", () => {
    expect(attachmentWorkspacePath("/workspace", "input-1", "../escape", "../file.txt"))
      .toBe("/workspace/.mohist/attachments/input-1/.._escape/file.txt")
    expect(attachmentWorkspacePath("/workspace", "..", "attachment-1", "file.txt"))
      .toBe("/workspace/.mohist/attachments/attachment/attachment-1/file.txt")
  })
})
