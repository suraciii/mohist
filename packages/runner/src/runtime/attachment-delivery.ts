import { dirname, join, resolve } from "node:path"
import { writeBinary } from "../system/process.js"
import { errorMessage } from "../core/errors.js"
import type { JsonObject } from "../core/types.js"
import type { AgentInputAttachmentContent, ServerConnection } from "../server/connection.js"
import type { RuntimeFilePart } from "./opencode/types.js"

/**
 * Runner-side attachment delivery (issue-513 / D4).
 *
 * Each accepted attachment descriptor carried on the dispatch
 * envelope is resolved into one of three delivery shapes:
 *
 *   1. A file materialized in the workspace under
 *      `.mohist/attachments/<inputId>/<id>/<name>` so the coding
 *      Agent reads it with its file tools (works for OpenCode and Pi).
 *   2. For image attachments on OpenCode, a native file part on the
 *      prompt body so the model can see the image directly.
 *   3. A system-attributed manifest block listing the provided files
 *      (name, type, size, workspace path). The block is appended to
 *      the composed prompt as factual metadata — never impersonating
 *      the user, never invented user intent. For attachment-only
 *      turns (no user text) the block is the turn-initiating content;
 *      it describes what was attached so the Agent knows the files
 *      exist without Mohist pretending the user said something.
 *
 * Content is fetched ONLY through the owning-input scoped route on
 * `ServerConnection.openAgentInputAttachment` — the runner never
 * receives or stores caller temp URLs, provider tokens, or raw
 * platform event payloads, so none of those can leak into the
 * instructions, reply, or transcript.
 *
 * A delivery-time fetch failure surfaces the attachment as
 * `unavailable` in the manifest (and skips the file part) so the
 * Agent sees the honest state — never a fabricated "delivered".
 */

export interface AttachmentDescriptor {
  readonly id: string
  readonly name: string
  readonly contentType: string | null
  readonly size: number
}

export interface AttachmentDeliveryContext {
  readonly projectId: string
  readonly agentSessionId: string
  readonly inputId: string
  readonly workDir: string
  readonly connection: ServerConnection | null
  readonly signal: AbortSignal
}

export type DeliveredAttachment =
  | {
      readonly descriptor: AttachmentDescriptor
      readonly status: "delivered"
      readonly workspacePath: string
      readonly filePart: RuntimeFilePart | null
    }
  | {
      readonly descriptor: AttachmentDescriptor
      readonly status: "unavailable"
      readonly reason: string
    }

export interface DeliveredAttachmentSet {
  readonly attachments: readonly DeliveredAttachment[]
  readonly manifestBlock: string
}

/**
 * Resolve every accepted attachment descriptor into a materialized
 * file plus an optional native file part, then build the manifest
 * block. An empty `descriptors` list returns an empty manifest — the
 * caller decides whether to append the (empty) block.
 *
 * Materialization is best-effort: each fetch failure is reported
 * honestly as `unavailable` and is excluded from both the file
 * materialization and the manifest's "delivered" rows. The block
 * still lists the unavailable rows so the Agent knows the input
 * claimed a file that could not be delivered.
 */
export async function deliverAcceptedAttachments(
  context: AttachmentDeliveryContext,
  descriptors: readonly AttachmentDescriptor[],
): Promise<DeliveredAttachmentSet> {
  if (descriptors.length === 0) {
    return { attachments: [], manifestBlock: "" }
  }

  if (!context.projectId || !context.agentSessionId || !context.inputId || !context.connection) {
    const reason = "no owning SessionInput scope is available on the dispatch; attachment delivery requires a scoped content route"
    const unavailable: DeliveredAttachment[] = descriptors.map((descriptor) => ({
      descriptor,
      status: "unavailable" as const,
      reason,
    }))
    return {
      attachments: unavailable,
      manifestBlock: buildManifestBlock(unavailable),
    }
  }

  const attachmentsRoot = attachmentWorkspaceRoot(context.workDir, context.inputId)
  const delivered: DeliveredAttachment[] = []

  for (const descriptor of descriptors) {
    let content: AgentInputAttachmentContent | null = null
    let failureReason: string | null = null
    try {
      content = await context.connection.openAgentInputAttachment(
        context.projectId,
        context.agentSessionId,
        context.inputId,
        descriptor.id,
        context.signal,
      )
    } catch (error) {
      failureReason = `attachment fetch failed: ${errorMessage(error)}`
    }

    if (!content && !failureReason) {
      failureReason = "attachment content is not available through the owning input's scoped path"
    }

    if (!content) {
      delivered.push({
        descriptor,
        status: "unavailable",
        reason: failureReason ?? "attachment content is not available through the owning input's scoped path",
      })
      continue
    }

    const workspacePath = resolve(attachmentsRoot, safePathSegment(descriptor.id), safeFileName(descriptor.name))
    try {
      await writeBinary(workspacePath, content.bytes)
    } catch (error) {
      delivered.push({
        descriptor,
        status: "unavailable",
        reason: `attachment materialization failed: ${errorMessage(error)}`,
      })
      continue
    }

    const filePart = buildImageFilePart(descriptor, content)
    delivered.push({
      descriptor,
      status: "delivered",
      workspacePath,
      filePart,
    })
  }

  return {
    attachments: delivered,
    manifestBlock: buildManifestBlock(delivered),
  }
}

/**
 * Render the system-attributed manifest block. The framing matches
 * the existing `[mohist-execution-definition]` block: a clearly
 * labeled, factual block listing the attachments that were provided
 * alongside this turn. The block NEVER contains caller temp URLs,
 * provider tokens, or raw platform event payloads — only the file
 * name, content type, size, and the local workspace path.
 */
export function buildManifestBlock(attachments: readonly DeliveredAttachment[]): string {
  if (attachments.length === 0) return ""

  const lines: string[] = []
  lines.push("[mohist-attachments]")
  lines.push("Mohist system metadata: the following files were accepted for this input. This metadata states what is available; it is not a user instruction.")
  for (const entry of attachments) {
    if (entry.status === "delivered") {
      lines.push(`- ${describeDescriptor(entry.descriptor)} available at ${entry.workspacePath}`)
    } else {
      lines.push(`- ${describeDescriptor(entry.descriptor)} unavailable: ${entry.reason}`)
    }
  }
  lines.push("[/mohist-attachments]")
  return lines.join("\n")
}

function describeDescriptor(descriptor: AttachmentDescriptor): string {
  const type = descriptor.contentType?.trim() || "application/octet-stream"
  return `${safeFileName(descriptor.name)} (${type}, ${descriptor.size} bytes)`
}

/**
 * Build a native file part for image attachments only. Non-image
 * attachments get `filePart: null` — the Agent reads them via the
 * workspace file path the manifest exposes.
 *
 * The data URL embeds the bytes directly so the request body
 * carries no caller temp URL, token, or external endpoint — just
 * the bytes the Runner fetched through the owning-input scoped
 * route.
 */
function buildImageFilePart(
  descriptor: AttachmentDescriptor,
  content: AgentInputAttachmentContent,
): RuntimeFilePart | null {
  const mime = descriptor.contentType?.trim() || content.contentType?.trim() || ""
  if (!mime.toLowerCase().startsWith("image/")) return null
  const dataUrl = `data:${mime};base64,${bufferToBase64(content.bytes)}`
  return { mime, filename: safeFileName(descriptor.name), url: dataUrl }
}

function safeFileName(name: string): string {
  const trimmed = name.replace(/[\u0000-\u001f\u007f]/g, "_").trim()
  if (trimmed.length === 0) return "attachment"
  // Disallow path traversal components — the resolved workspace
  // path is joined with this name, and a hostile value must not be
  // able to escape the per-input directory.
  const segments = trimmed.split(/[\\/]+/).filter((segment) => segment.length > 0 && segment !== "." && segment !== "..")
  return segments.length > 0 ? segments.join("_") : "attachment"
}

function safePathSegment(value: string): string {
  const segment = value.replace(/[^a-zA-Z0-9._-]/g, "_")
  return segment.length > 0 && segment !== "." && segment !== ".." ? segment : "attachment"
}

function bufferToBase64(bytes: Uint8Array): string {
  if (typeof Buffer !== "undefined") {
    return Buffer.from(bytes).toString("base64")
  }
  let binary = ""
  for (let i = 0; i < bytes.byteLength; i++) {
    binary += String.fromCharCode(bytes[i]!)
  }
  // btoa is available in Node ≥ 16 on globalThis.
  return globalThis.btoa(binary)
}

export function attachmentWorkspaceRoot(workDir: string, inputId: string): string {
  return join(workDir, ".mohist", "attachments", safePathSegment(inputId))
}

export function attachmentWorkspacePath(workDir: string, inputId: string, attachmentId: string, name: string): string {
  return join(attachmentWorkspaceRoot(workDir, inputId), safePathSegment(attachmentId), safeFileName(name))
}

export function attachmentParentDirectory(workspacePath: string): string {
  return dirname(workspacePath)
}

/**
 * Read attachment descriptors off a dispatch `with` payload. The
 * wire shape is `{ id, name, contentType, size }` — bytes are never
 * carried. Returns an empty array when the payload is absent or the
 * field is missing / malformed, matching the server's permissive
 * contract: legacy dispatches without attachments stay text-only.
 */
export function readAttachmentDescriptors(payload: JsonObject | null | undefined): AttachmentDescriptor[] {
  if (!payload) return []
  return parseAttachmentDescriptors(payload["attachments"])
}

export function parseAttachmentDescriptors(value: unknown): AttachmentDescriptor[] {
  if (!Array.isArray(value)) return []
  const descriptors: AttachmentDescriptor[] = []
  for (const entry of value) {
    if (!isObject(entry)) continue
    const id = entry["id"]
    const name = entry["name"]
    const size = entry["size"]
    if (typeof id !== "string" || id.length === 0) continue
    if (typeof name !== "string" || name.length === 0) continue
    if (typeof size !== "number" || !Number.isFinite(size) || size < 0) continue
    const contentType = entry["contentType"]
    descriptors.push({
      id,
      name,
      contentType: typeof contentType === "string" ? contentType : null,
      size,
    })
  }
  return descriptors
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
}

/**
 * Compose the final turn input by appending the system-attributed
 * attachment manifest block to the existing execution envelope. The
 * block is omitted entirely when there are no attachments so the
 * prompt text stays byte-identical to the pre-attachment path.
 */
export function attachmentManifestEnvelope(
  envelope: string,
  attachments: readonly DeliveredAttachment[],
): string {
  const manifestBlock = buildManifestBlock(attachments)
  if (!manifestBlock) return envelope
  return envelope ? `${envelope}\n\n${manifestBlock}` : manifestBlock
}

/**
 * Build a delivery context for AgentJob dispatches. The Runner uses
 * `work.initialInputId` (issue-512 T-001: stable id minted by the
 * Server before dispatch) as the owning-input scope. When the
 * dispatch does not yet carry the stable input id the function
 * returns `null` so the caller skips materialization — the Server
 * carries no owning input, the manifest block would be empty, and
 * any bytes the Runner fetched would not have a place to live.
 */
export function buildAttachmentContext(
  connection: ServerConnection,
  work: {
    projectId?: string | null
    agentSessionId?: string | null
    initialInputId?: string | null
  },
  workDir: string,
  signal: AbortSignal,
): AttachmentDeliveryContext {
  const projectId = work.projectId ?? ""
  const agentSessionId = work.agentSessionId ?? ""
  const inputId = work.initialInputId ?? ""
  return { projectId, agentSessionId, inputId, workDir, connection, signal }
}
