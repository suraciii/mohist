import type {
  Delivery,
  DeliveryAck,
  ProviderMessageIdentity,
  SlackFileUploadResponse,
  SlackWebClient,
} from './types.js'
import { isRecord, stringValue } from './adapter-events.js'

export interface DeliveryPayload {
  readonly operation?: string
  readonly text?: string
  readonly clientMessageId?: string
  readonly providerMessageIdentity?: ProviderMessageIdentity
  readonly targetMessageIdentity?: ProviderMessageIdentity
  readonly reaction?: string
  readonly fallbackText?: string
  readonly fallbackDispatchRef?: string
  readonly statusDispatchRef?: string
  readonly blocks?: readonly Record<string, unknown>[]
  readonly fileName?: string
  readonly fileContentBase64?: string
  readonly segments?: readonly string[]
}

export function parseDeliveryPayload(value: string): DeliveryPayload {
  const parsed: unknown = JSON.parse(value)
  if (!isRecord(parsed)) throw new Error('Delivery payload was not an object')
  return parsed as DeliveryPayload
}

export function requiredText(payload: DeliveryPayload): string {
  if (!payload.text) throw new Error('Delivery payload did not contain text')
  return payload.text
}

export function delivered(delivery: Delivery, identity?: ProviderMessageIdentity): DeliveryAck {
  return identity
    ? { id: delivery.id, outcome: 'delivered', providerMessageIdentity: identity }
    : { id: delivery.id, outcome: 'delivered' }
}

export function withAdapterId(ack: DeliveryAck, adapterId: string): DeliveryAck {
  return { ...ack, adapterId }
}

export function isKnownDeliveryOperation(
  operation: unknown,
): operation is 'post_message' | 'chat_update' | 'reaction_add' | 'reaction_remove' | 'upload_file' {
  return (
    operation === 'post_message' ||
    operation === 'chat_update' ||
    operation === 'reaction_add' ||
    operation === 'reaction_remove' ||
    operation === 'upload_file'
  )
}

export function isUnsupportedReactionError(error: string | undefined): boolean {
  return new Set([
    'cant_react',
    'message_not_found',
    'not_in_channel',
    'not_allowed_token_type',
    'invalid_timestamp',
    'channel_not_found',
    'missing_scope',
  ]).has(error ?? '')
}

export async function mutateReaction(
  web: SlackWebClient,
  operation: 'reaction_add' | 'reaction_remove',
  target: ProviderMessageIdentity,
  reaction: string,
  ensureCurrent: () => void,
) {
  const method = operation === 'reaction_add' ? web.reactions?.add : web.reactions?.remove
  try {
    ensureCurrent()
    const response = await method?.call(web.reactions, {
      channel: target.conversationId,
      name: reaction,
      timestamp: target.messageTs,
    })
    ensureCurrent()
    return response
  } catch (error) {
    ensureCurrent()
    const code = slackErrorCode(error)
    if (!code) throw error
    return { ok: false, error: code }
  }
}

export async function getReaction(
  web: SlackWebClient | undefined,
  target: ProviderMessageIdentity,
  ensureCurrent: () => void,
) {
  try {
    ensureCurrent()
    const response = await web?.reactions?.get?.({
      channel: target.conversationId,
      timestamp: target.messageTs,
      full: true,
    })
    ensureCurrent()
    return response
  } catch (error) {
    ensureCurrent()
    const code = slackErrorCode(error)
    if (!code) throw error
    return { ok: false, error: code }
  }
}

function slackErrorCode(error: unknown): string | undefined {
  if (isRecord(error)) {
    const data = isRecord(error.data) ? error.data : undefined
    const dataError = stringValue(data?.error)
    if (dataError) return dataError
    const directError = stringValue(error.error)
    if (directError) return directError
    const message = stringValue(error.message)
    if (message) return slackErrorCodeFromMessage(message)
  }
  return error instanceof Error ? slackErrorCodeFromMessage(error.message) : undefined
}

function slackErrorCodeFromMessage(message: string): string | undefined {
  return message.match(/API error occurred:\s*([a-z][a-z0-9_]*)/i)?.[1]
}

export async function findStatusMessage(
  web: SlackWebClient,
  conversationId: string,
  clientMessageId: string,
  ensureCurrent: () => void,
): Promise<ProviderMessageIdentity | undefined> {
  ensureCurrent()
  const history = await web.conversations?.history?.({ channel: conversationId, limit: 200 })
  ensureCurrent()
  if (!history || history.ok === false) return undefined
  const message = history.messages?.find((candidate) => candidate.client_msg_id === clientMessageId && candidate.ts)
  return message?.ts ? { conversationId, messageTs: message.ts } : undefined
}

export async function mutateDelivery(
  web: SlackWebClient,
  delivery: Delivery,
  ensureCurrent: () => void,
): Promise<DeliveryAck> {
  ensureCurrent()
  const payload = parseDeliveryPayload(delivery.payloadJson)
  const operation = payload.operation ?? 'post_message'
  if (!isKnownDeliveryOperation(operation)) {
    const ack = await reconcile(web, delivery, ensureCurrent)
    ensureCurrent()
    return ack
  }
  const segments = Array.isArray(payload.segments) && payload.segments.length > 1 ? payload.segments : undefined
  if (segments) {
    const ack = await deliverSegments(web, delivery, payload, segments, ensureCurrent)
    ensureCurrent()
    return ack
  }
  if (operation === 'chat_update') {
    const target = payload.providerMessageIdentity
    if (!target) throw new Error('chat.update delivery has no provider message identity')
    ensureCurrent()
    const response = await web.chat.update?.({
      channel: target.conversationId,
      ts: target.messageTs,
      text: requiredText(payload),
      ...(payload.blocks ? { blocks: payload.blocks } : {}),
    })
    ensureCurrent()
    if (!response) throw new Error('Slack client does not support chat.update')
    if (response.ok === false) {
      const ack = await fallbackAfterUpdateFailure(
        web,
        delivery,
        payload,
        response.error ?? 'Slack rejected chat.update',
        ensureCurrent,
      )
      ensureCurrent()
      return ack
    }
    return delivered(delivery, { conversationId: target.conversationId, messageTs: response.ts ?? target.messageTs })
  }

  if (operation === 'reaction_add' || operation === 'reaction_remove') {
    const target = payload.targetMessageIdentity
    if (!target || !payload.reaction) throw new Error(`${operation} delivery is missing its target`)
    ensureCurrent()
    const response = await mutateReaction(web, operation, target, payload.reaction, ensureCurrent)
    ensureCurrent()
    if (!response) throw new Error('Slack client does not support reactions')
    if (response.ok === false) {
      if (!isUnsupportedReactionError(response.error))
        return { id: delivery.id, outcome: 'retry', reason: response.error ?? 'Slack rejected the reaction' }
      if (response.error === 'missing_scope') {
        if (!payload.fallbackText || !payload.fallbackDispatchRef) return delivered(delivery)
        const ack = await postFallback(web, delivery, payload, response.error, ensureCurrent)
        ensureCurrent()
        return ack
      }
      const statusTarget = payload.statusDispatchRef
        ? await findStatusMessage(web, delivery.conversationId, payload.statusDispatchRef, ensureCurrent)
        : undefined
      ensureCurrent()
      if (statusTarget && statusTarget.messageTs !== target.messageTs) {
        ensureCurrent()
        const statusResponse = await mutateReaction(web, operation, statusTarget, payload.reaction, ensureCurrent)
        ensureCurrent()
        if (!statusResponse) throw new Error('Slack client does not support reactions')
        if (statusResponse.ok !== false) return delivered(delivery)
        if (!isUnsupportedReactionError(statusResponse.error))
          return { id: delivery.id, outcome: 'retry', reason: statusResponse.error ?? 'Slack rejected the reaction' }
      }
      if (operation === 'reaction_remove') return delivered(delivery)
      if (payload.fallbackText && payload.fallbackDispatchRef) {
        const ack = await postFallback(
          web,
          delivery,
          payload,
          response.error ?? 'Slack does not support reactions',
          ensureCurrent,
        )
        ensureCurrent()
        return ack
      }
      return delivered(delivery)
    }
    return delivered(delivery)
  }

  if (operation === 'upload_file') {
    const ack = await uploadFile(web, delivery, payload, ensureCurrent)
    ensureCurrent()
    return ack
  }

  const text = payload.text ?? (payload.blocks && payload.blocks.length > 0 ? '' : requiredText(payload))
  const existingStatus = payload.statusDispatchRef
    ? await findStatusMessage(web, delivery.conversationId, payload.statusDispatchRef, ensureCurrent)
    : undefined
  ensureCurrent()
  if (existingStatus && web.chat.update) {
    ensureCurrent()
    const response = await web.chat.update({
      channel: existingStatus.conversationId,
      ts: existingStatus.messageTs,
      text,
      ...(payload.blocks ? { blocks: payload.blocks } : {}),
    })
    ensureCurrent()
    if (response.ok === false)
      return { id: delivery.id, outcome: 'retry', reason: response.error ?? 'Slack rejected the status update' }
    return delivered(delivery, {
      conversationId: existingStatus.conversationId,
      messageTs: response.ts ?? existingStatus.messageTs,
    })
  }
  ensureCurrent()
  const response = await web.chat.postMessage({
    channel: delivery.conversationId,
    text,
    ...(delivery.threadTs ? { thread_ts: delivery.threadTs } : {}),
    ...(payload.clientMessageId ? { client_msg_id: payload.clientMessageId } : {}),
    ...(payload.blocks ? { blocks: payload.blocks } : {}),
  })
  ensureCurrent()
  if (response.ok === false)
    return { id: delivery.id, outcome: 'retry', reason: response.error ?? 'Slack rejected the post' }
  return delivered(
    delivery,
    response.ts ? { conversationId: delivery.conversationId, messageTs: response.ts } : undefined,
  )
}

async function deliverSegments(
  web: SlackWebClient,
  delivery: Delivery,
  payload: DeliveryPayload,
  segments: readonly string[],
  ensureCurrent: () => void,
): Promise<DeliveryAck> {
  const thread_ts = delivery.threadTs ?? undefined
  let firstIdentity: ProviderMessageIdentity | undefined
  for (let index = 0; index < segments.length; index++) {
    ensureCurrent()
    const response = await web.chat.postMessage({
      channel: delivery.conversationId,
      text: segments[index]!,
      ...(thread_ts ? { thread_ts } : {}),
      ...(index === 0 && payload.clientMessageId ? { client_msg_id: payload.clientMessageId } : {}),
    })
    ensureCurrent()
    if (response.ok === false)
      return { id: delivery.id, outcome: 'retry', reason: response.error ?? 'Slack rejected a segmented post' }
    if (index === 0 && response.ts) firstIdentity = { conversationId: delivery.conversationId, messageTs: response.ts }
  }
  return delivered(delivery, firstIdentity)
}

async function uploadFile(
  web: SlackWebClient,
  delivery: Delivery,
  payload: DeliveryPayload,
  ensureCurrent: () => void,
): Promise<DeliveryAck> {
  if (!web.filesUploadV2 || !payload.fileName || !payload.fileContentBase64)
    throw new Error('upload_file delivery is missing the Slack upload client or file payload')
  ensureCurrent()
  const response = await web.filesUploadV2({
    ...(delivery.threadTs
      ? { channels: delivery.conversationId, thread_ts: delivery.threadTs }
      : { channel_id: delivery.conversationId }),
    filename: payload.fileName,
    file: Buffer.from(payload.fileContentBase64, 'base64'),
    ...(payload.text ? { initial_comment: payload.text } : {}),
  })
  ensureCurrent()
  if (response.ok === false)
    return { id: delivery.id, outcome: 'retry', reason: response.error ?? 'Slack rejected the file upload' }
  const identity = await fileShareIdentity(web, delivery, response, ensureCurrent)
  ensureCurrent()
  return identity ? delivered(delivery, identity) : delivered(delivery)
}

async function fileShareIdentity(
  web: SlackWebClient,
  delivery: Delivery,
  response: SlackFileUploadResponse,
  ensureCurrent: () => void,
): Promise<ProviderMessageIdentity | undefined> {
  const file = response.files?.[0]?.files?.[0]
  const ts =
    file?.shares?.public?.[delivery.conversationId]?.[0]?.ts ??
    file?.shares?.private?.[delivery.conversationId]?.[0]?.ts
  if (ts) return { conversationId: delivery.conversationId, messageTs: ts }
  if (!file?.id) return undefined
  ensureCurrent()
  const history = await web.conversations?.history?.({ channel: delivery.conversationId, limit: 200 })
  ensureCurrent()
  const share = history?.messages?.find(
    (candidate) => candidate.files?.some((candidateFile) => candidateFile.id === file.id) && candidate.ts,
  )
  return share?.ts ? { conversationId: delivery.conversationId, messageTs: share.ts } : undefined
}

async function fallbackAfterUpdateFailure(
  web: SlackWebClient,
  delivery: Delivery,
  payload: DeliveryPayload,
  reason: string,
  ensureCurrent: () => void,
) {
  if (!payload.fallbackText || !payload.fallbackDispatchRef)
    return { id: delivery.id, outcome: 'retry' as const, reason }
  const ack = await postFallback(web, delivery, payload, reason, ensureCurrent)
  ensureCurrent()
  return ack
}

async function postFallback(
  web: SlackWebClient,
  delivery: Delivery,
  payload: DeliveryPayload,
  reason: string,
  ensureCurrent: () => void,
): Promise<DeliveryAck> {
  ensureCurrent()
  const response = await web.chat.postMessage({
    channel: delivery.conversationId,
    text: payload.fallbackText ?? payload.text ?? '',
    ...(delivery.threadTs ? { thread_ts: delivery.threadTs } : {}),
    client_msg_id: payload.fallbackDispatchRef,
    ...(payload.blocks ? { blocks: payload.blocks } : {}),
  })
  ensureCurrent()
  if (response.ok === false) return { id: delivery.id, outcome: 'retry', reason: response.error ?? reason }
  return delivered(
    delivery,
    response.ts ? { conversationId: delivery.conversationId, messageTs: response.ts } : undefined,
  )
}

export async function reconcile(
  web: SlackWebClient | undefined,
  delivery: Delivery,
  ensureCurrent: () => void,
): Promise<DeliveryAck> {
  ensureCurrent()
  const payload = parseDeliveryPayload(delivery.payloadJson)
  const target = payload.providerMessageIdentity ?? payload.targetMessageIdentity
  if ((payload.operation === 'reaction_add' || payload.operation === 'reaction_remove') && target && payload.reaction) {
    const response = await getReaction(web, target, ensureCurrent)
    ensureCurrent()
    if (!web || !response)
      return { id: delivery.id, outcome: 'uncertain', reason: 'Slack client cannot reconcile reactions' }
    if (response.ok === false) {
      if (!isUnsupportedReactionError(response.error))
        return {
          id: delivery.id,
          outcome: 'uncertain',
          reason: response.error ?? 'Slack reaction reconciliation failed',
        }
      if (!payload.fallbackText || !payload.fallbackDispatchRef) return delivered(delivery, target)
      const ack = await postFallback(
        web,
        delivery,
        payload,
        response.error ?? 'Slack reaction reconciliation failed',
        ensureCurrent,
      )
      ensureCurrent()
      return ack
    }
    const present = response.message?.reactions?.some((reaction) => reaction.name === payload.reaction)
    const deliveredState = payload.operation === 'reaction_add' ? present : !present
    return deliveredState
      ? delivered(delivery, target)
      : { id: delivery.id, outcome: 'retry', reason: 'provider_mutation_absent' }
  }

  ensureCurrent()
  const history = await web?.conversations?.history?.({
    channel: delivery.conversationId,
    ...(target ? { latest: target.messageTs, oldest: target.messageTs } : {}),
    inclusive: true,
    limit: 200,
  })
  ensureCurrent()
  if (!history) return { id: delivery.id, outcome: 'uncertain', reason: 'Slack client cannot reconcile messages' }
  if (history.ok === false)
    return { id: delivery.id, outcome: 'uncertain', reason: history.error ?? 'Slack message reconciliation failed' }
  const message = history.messages?.find(
    (candidate) =>
      (target && candidate.ts === target.messageTs) ||
      (payload.clientMessageId && candidate.client_msg_id === payload.clientMessageId) ||
      (payload.fallbackDispatchRef && candidate.client_msg_id === payload.fallbackDispatchRef),
  )
  if (message?.ts) {
    if (payload.operation === 'chat_update' && payload.text && message.text !== payload.text)
      return { id: delivery.id, outcome: 'retry', reason: 'provider_mutation_absent' }
    return isKnownDeliveryOperation(payload.operation ?? 'post_message')
      ? delivered(delivery, { conversationId: delivery.conversationId, messageTs: message.ts })
      : delivered(delivery)
  }
  if (payload.operation === 'chat_update' && payload.fallbackText && payload.fallbackDispatchRef) {
    const fallbackWeb = web
    if (!fallbackWeb) return { id: delivery.id, outcome: 'uncertain', reason: 'Slack client cannot post fallback' }
    const ack = await postFallback(fallbackWeb, delivery, payload, 'provider_mutation_absent', ensureCurrent)
    ensureCurrent()
    return ack
  }
  return { id: delivery.id, outcome: 'retry', reason: 'provider_mutation_absent' }
}
