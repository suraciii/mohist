import type {
  SlackEnvelope,
  SlackFileRef,
  SlackInteractionEnvelope,
  SlackManagerRef,
  SlackSenderKind,
  SlackAdapterTarget,
} from './types.js'

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function stringValue(value: unknown, key?: string): string | null {
  const candidate = key && isRecord(value) ? value[key] : value
  return typeof candidate === 'string' && candidate.length > 0 ? candidate : null
}

export function connectionKey(ref: SlackAdapterTarget) {
  return isManagerTarget(ref) ? `manager:${ref.enrollmentId}` : `connection:${ref.projectId}:${ref.connectionId}`
}

function isManagerTarget(value: SlackAdapterTarget): value is SlackManagerRef {
  return value.kind === 'manager'
}

export function normalizeSocketEvent(body: unknown): SlackEnvelope {
  const event = isRecord(body) && isRecord(body.event) ? body.event : body
  if (!isRecord(event)) throw new Error('Slack Socket Mode event is malformed')
  const apiAppId = stringValue(event.api_app_id) ?? stringValue(body, 'api_app_id')
  const teamId = stringValue(event.team_id) ?? stringValue(body, 'team_id')
  const conversationId = stringValue(event.channel)
  const messageTs = stringValue(event.ts) ?? stringValue(event.event_ts)
  if (!apiAppId || !teamId || !conversationId || !messageTs)
    throw new Error('Slack event is missing its stable identity')
  const senderKind = normalizeSenderKind(event)
  return {
    eventType: stringValue(event.type) ?? 'message',
    apiAppId,
    isDirectMessage: event.channel_type === 'im' || conversationId.startsWith('D'),
    teamId,
    conversationId,
    messageTs,
    threadTs: stringValue(event.thread_ts),
    mentionedUserIds: parseMentionedUserIds(typeof event.text === 'string' ? event.text : null),
    senderSlackUserId: senderKind === 'human' ? stringValue(event.user) : null,
    senderKind,
    authorBot: senderKind === 'bot' ? normalizeBotAuthor(event) : null,
    text: typeof event.text === 'string' ? event.text : null,
    files: parseFiles(event.files),
  }
}

export function normalizeSlackInteraction(body: unknown): SlackInteractionEnvelope {
  const payload = interactionPayload(body)
  if (!payload || payload.type !== 'block_actions') throw new Error('Slack interaction is malformed')
  const apiAppId = stringValue(payload.api_app_id)
  const team = isRecord(payload.team) ? stringValue(payload.team.id) : stringValue(payload, 'team_id')
  const user = isRecord(payload.user) ? stringValue(payload.user.id) : null
  const container = isRecord(payload.container) ? payload.container : undefined
  const conversationId = stringValue(container?.channel_id)
  const messageTs = stringValue(container?.message_ts)
  const actions = Array.isArray(payload.actions) ? payload.actions : []
  const action = actions.length > 0 && isRecord(actions[0]) ? actions[0] : undefined
  const interactionId =
    stringValue(payload.trigger_id) ?? stringValue(action?.action_ts) ?? stringValue(payload, 'event_id')
  const actionId = stringValue(action?.action_id)
  const actionValue = stringValue(action?.value)
  if (!apiAppId || !team || !user || !conversationId || !messageTs || !interactionId || !actionId || !actionValue)
    throw new Error('Slack interaction is missing its stable identity')
  return {
    eventType: 'block_actions',
    apiAppId,
    interactionId,
    teamId: team,
    conversationId,
    messageTs,
    threadTs: stringValue(container?.thread_ts),
    actorSlackUserId: user,
    actionId,
    actionValue,
  }
}

export function isSlackInteraction(value: unknown): value is Record<string, unknown> {
  return interactionPayload(value)?.type === 'block_actions'
}

export function slackEventType(value: unknown): string {
  const interaction = interactionPayload(value)
  if (interaction) return stringValue(interaction.type) ?? 'interactive'
  const event = isRecord(value) && isRecord(value.event) ? value.event : value
  return stringValue(event, 'type') ?? 'unknown'
}

function interactionPayload(value: unknown): Record<string, unknown> | null {
  if (!isRecord(value)) return null
  if (value.type === 'block_actions') return value
  if (value.type !== 'interactive') return null
  const payload = value.payload
  if (isRecord(payload)) return payload
  if (typeof payload !== 'string') return null
  try {
    const parsed: unknown = JSON.parse(payload)
    return isRecord(parsed) ? parsed : null
  } catch {
    return null
  }
}

function parseFiles(value: unknown): readonly SlackFileRef[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((candidate) => {
    if (!isRecord(candidate)) return []
    const id = stringValue(candidate.id)
    const name = stringValue(candidate.name)
    const mimetype = stringValue(candidate.mimetype)
    const size = candidate.size
    return id && name && mimetype && typeof size === 'number' && Number.isSafeInteger(size) && size >= 0
      ? [{ id, name, mimetype, size }]
      : []
  })
}

function normalizeSenderKind(event: Record<string, unknown>): SlackSenderKind {
  if (stringValue(event.bot_id) || stringValue(event.subtype) === 'bot_message' || isRecord(event.bot_profile))
    return 'bot'
  return stringValue(event.user) ? 'human' : 'unknown'
}

function normalizeBotAuthor(event: Record<string, unknown>): SlackEnvelope['authorBot'] {
  const botProfile = isRecord(event.bot_profile) ? event.bot_profile : null
  const eventAppId = stringValue(event.app_id)
  const profileAppId = stringValue(botProfile?.app_id)
  const eventBotId = stringValue(event.bot_id)
  const profileBotId = stringValue(botProfile?.id)
  const appId = eventAppId ?? profileAppId
  const botId = eventBotId ?? profileBotId
  const identityConflict =
    (eventAppId !== null && profileAppId !== null && eventAppId !== profileAppId) ||
    (eventBotId !== null && profileBotId !== null && eventBotId !== profileBotId)
  const botUserId = stringValue(event.user)
  if (appId === null && botId === null && botUserId === null && !identityConflict) return null
  return { appId, botId, botUserId, identityConflict }
}

function parseMentionedUserIds(text: string | null): readonly string[] {
  if (!text) return []
  const mentioned = new Set<string>()
  const pattern = /<@([A-Za-z0-9_-]+)(?:\|[^>]*)?>/g
  for (const match of text.matchAll(pattern)) {
    const userId = match[1]
    if (userId) mentioned.add(userId)
  }
  return [...mentioned]
}
