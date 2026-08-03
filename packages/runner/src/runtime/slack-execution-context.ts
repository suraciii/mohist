import { createHash } from "node:crypto"
import type { ResolvedSkill } from "./skill-resolver.js"

export interface SlackExecutionContext {
  readonly version: number
  readonly replyAnchor: {
    readonly workspaceId: string
    readonly conversationId: string
    readonly threadRootMessageId: string
    readonly triggeringMessageId: string
    readonly initiatingMemberId: string
    readonly connectionId: string
    readonly sessionId: string
    readonly dispatchRef: string
  }
  readonly collaborationSkill: {
    readonly name: string
    readonly version: string
    readonly instructions: string
    readonly contentHash: string
  }
}

export type SlackExecutionContextRead =
  | { readonly kind: "absent" }
  | { readonly kind: "invalid"; readonly message: string }
  | { readonly kind: "resolved"; readonly value: SlackExecutionContext }

export function readSlackExecutionContext(payload: { readonly slackExecutionContext?: unknown } | null): SlackExecutionContextRead {
  const raw = payload?.slackExecutionContext
  if (raw === undefined || raw === null) return { kind: "absent" }
  if (!isRecord(raw)) return { kind: "invalid", message: "slackExecutionContext must be an object" }

  const replyAnchor = raw["replyAnchor"]
  const collaborationSkill = raw["collaborationSkill"]
  if (raw["version"] !== 1 || !isRecord(replyAnchor) || !isRecord(collaborationSkill))
    return { kind: "invalid", message: "slackExecutionContext has an unsupported shape" }

  const anchorFields = [
    "workspaceId",
    "conversationId",
    "threadRootMessageId",
    "triggeringMessageId",
    "initiatingMemberId",
    "connectionId",
    "sessionId",
    "dispatchRef",
  ] as const
  if (anchorFields.some((field) => !nonEmptyString(replyAnchor[field])))
    return { kind: "invalid", message: "slackExecutionContext.replyAnchor is incomplete" }

  if (!nonEmptyString(collaborationSkill["name"])
    || !nonEmptyString(collaborationSkill["version"])
    || !nonEmptyString(collaborationSkill["instructions"])
    || !nonEmptyString(collaborationSkill["contentHash"]))
    return { kind: "invalid", message: "slackExecutionContext.collaborationSkill is incomplete" }

  const instructions = collaborationSkill["instructions"] as string
  const contentHash = collaborationSkill["contentHash"] as string
  const expectedHash = createHash("sha256").update(instructions, "utf8").digest("hex")
  if (contentHash !== expectedHash)
    return { kind: "invalid", message: "slackExecutionContext collaboration skill hash does not match its content" }

  return {
    kind: "resolved",
    value: {
      version: 1,
      replyAnchor: {
        workspaceId: replyAnchor["workspaceId"] as string,
        conversationId: replyAnchor["conversationId"] as string,
        threadRootMessageId: replyAnchor["threadRootMessageId"] as string,
        triggeringMessageId: replyAnchor["triggeringMessageId"] as string,
        initiatingMemberId: replyAnchor["initiatingMemberId"] as string,
        connectionId: replyAnchor["connectionId"] as string,
        sessionId: replyAnchor["sessionId"] as string,
        dispatchRef: replyAnchor["dispatchRef"] as string,
      },
      collaborationSkill: {
        name: collaborationSkill["name"] as string,
        version: collaborationSkill["version"] as string,
        instructions,
        contentHash,
      },
    },
  }
}

export function inlineSlackCollaborationSkill(context: SlackExecutionContext): ResolvedSkill {
  return {
    name: context.collaborationSkill.name,
    instructions: context.collaborationSkill.instructions,
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0
}
