import { createHash } from 'node:crypto'
import type { ResolvedSkill } from './skill-resolver.js'

export const SLACK_EXECUTION_SOURCE = 'slack' as const
export const NON_SLACK_EXECUTION_SOURCE = 'non-slack' as const
export const PUBLISHED_SLACK_SKILL_NAME = 'mohist-slack-collaboration' as const
export const PUBLISHED_SLACK_SKILL_VERSION = '1.0.0' as const
export const PUBLISHED_SLACK_SKILL_HASH = 'dedf18a796543ade06a9e0ece00c086577153e1e633f868c099b01cf910d641b' as const

export type ExecutionSource = typeof SLACK_EXECUTION_SOURCE | typeof NON_SLACK_EXECUTION_SOURCE

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
  | { readonly kind: 'absent' }
  | { readonly kind: 'invalid'; readonly message: string }
  | { readonly kind: 'resolved'; readonly value: SlackExecutionContext }

export type ExecutionSourceContextRead =
  | { readonly kind: 'legacy'; readonly slackExecutionContext: SlackExecutionContext | null }
  | {
      readonly kind: 'resolved'
      readonly source: ExecutionSource
      readonly slackExecutionContext: SlackExecutionContext | null
    }
  | { readonly kind: 'invalid'; readonly message: string }

export interface ExecutionSourceContextValidationOptions {
  readonly strict?: boolean
}

/**
 * Validates the source/context pair once for all Runner ingress paths.
 * Source-less payloads are retained as an explicit legacy result only while
 * strict validation is disabled; they are never normalized to non-Slack.
 */
export function readExecutionSourceContext(
  payload: { readonly executionSource?: unknown; readonly slackExecutionContext?: unknown } | null,
  options: ExecutionSourceContextValidationOptions = {},
): ExecutionSourceContextRead {
  const sourcePresent = payload !== null && Object.prototype.hasOwnProperty.call(payload, 'executionSource')
  const source = payload?.executionSource

  // Before source v1, Slack payloads were source-less and their context
  // accepted any self-consistent Skill snapshot. Preserve that exact wire
  // contract only when the discriminator is genuinely absent.
  if (!sourcePresent) {
    if (options.strict) return invalid('executionSource is required')
    const context = readLegacySlackExecutionContext(payload)
    if (context.kind === 'invalid') return context
    return {
      kind: 'legacy',
      slackExecutionContext: context.kind === 'resolved' ? context.value : null,
    }
  }

  if (source === undefined || source === null) return invalid('executionSource is required')
  if (source !== SLACK_EXECUTION_SOURCE && source !== NON_SLACK_EXECUTION_SOURCE)
    return invalid('executionSource must be slack or non-slack')

  const context = readSlackExecutionContext(payload)
  if (source === NON_SLACK_EXECUTION_SOURCE) {
    if (
      context.kind === 'resolved' ||
      (payload?.slackExecutionContext !== undefined && payload.slackExecutionContext !== null)
    )
      return invalid('non-slack execution cannot carry a Slack execution context')
    if (context.kind === 'invalid') return context
    return { kind: 'resolved', source, slackExecutionContext: null }
  }

  if (context.kind === 'absent') return invalid('slack execution requires a complete execution context')
  if (context.kind === 'invalid') return context
  return { kind: 'resolved', source, slackExecutionContext: context.value }
}

export function readSlackExecutionContext(
  payload: { readonly slackExecutionContext?: unknown } | null,
): SlackExecutionContextRead {
  return readSlackExecutionContextShape(payload, true)
}

function readLegacySlackExecutionContext(
  payload: { readonly slackExecutionContext?: unknown } | null,
): SlackExecutionContextRead {
  return readSlackExecutionContextShape(payload, false)
}

function readSlackExecutionContextShape(
  payload: { readonly slackExecutionContext?: unknown } | null,
  requirePublishedSkill: boolean,
): SlackExecutionContextRead {
  const raw = payload?.slackExecutionContext
  if (raw === undefined || raw === null) return { kind: 'absent' }
  if (!isRecord(raw)) return invalid('slackExecutionContext must be an object')

  const replyAnchor = raw.replyAnchor
  const collaborationSkill = raw.collaborationSkill
  if (raw.version !== 1 || !isRecord(replyAnchor) || !isRecord(collaborationSkill))
    return invalid('slackExecutionContext has an unsupported shape')

  const anchorFields = [
    'workspaceId',
    'conversationId',
    'threadRootMessageId',
    'triggeringMessageId',
    'initiatingMemberId',
    'connectionId',
    'sessionId',
    'dispatchRef',
  ] as const
  if (anchorFields.some((field) => !nonEmptyString(replyAnchor[field])))
    return invalid('slackExecutionContext.replyAnchor is incomplete')

  if (
    !nonEmptyString(collaborationSkill.name) ||
    !nonEmptyString(collaborationSkill.version) ||
    !nonEmptyString(collaborationSkill.instructions) ||
    !nonEmptyString(collaborationSkill.contentHash)
  )
    return invalid('slackExecutionContext.collaborationSkill is incomplete')

  if (
    requirePublishedSkill &&
    (collaborationSkill.name !== PUBLISHED_SLACK_SKILL_NAME ||
      collaborationSkill.version !== PUBLISHED_SLACK_SKILL_VERSION)
  )
    return invalid('slackExecutionContext uses an unpublished collaboration Skill identity')

  const instructions = collaborationSkill.instructions
  const contentHash = collaborationSkill.contentHash
  if (requirePublishedSkill && !/^[a-f0-9]{64}$/.test(contentHash))
    return invalid('slackExecutionContext collaboration skill contentHash must be lowercase hexadecimal')

  const expectedHash = createHash('sha256').update(instructions, 'utf8').digest('hex')
  if (contentHash !== expectedHash)
    return invalid('slackExecutionContext collaboration skill hash does not match its content')
  if (requirePublishedSkill && contentHash !== PUBLISHED_SLACK_SKILL_HASH)
    return invalid('slackExecutionContext collaboration skill hash does not match the published Skill')

  return {
    kind: 'resolved',
    value: {
      version: 1,
      replyAnchor: {
        workspaceId: replyAnchor.workspaceId as string,
        conversationId: replyAnchor.conversationId as string,
        threadRootMessageId: replyAnchor.threadRootMessageId as string,
        triggeringMessageId: replyAnchor.triggeringMessageId as string,
        initiatingMemberId: replyAnchor.initiatingMemberId as string,
        connectionId: replyAnchor.connectionId as string,
        sessionId: replyAnchor.sessionId as string,
        dispatchRef: replyAnchor.dispatchRef as string,
      },
      collaborationSkill: {
        name: collaborationSkill.name as string,
        version: collaborationSkill.version as string,
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

function invalid(message: string): { readonly kind: 'invalid'; readonly message: string } {
  return { kind: 'invalid', message }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}
