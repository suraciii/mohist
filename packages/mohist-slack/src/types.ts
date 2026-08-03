export interface SlackConnectionRef {
  readonly projectId: string
  readonly connectionId: string
}

export interface AdapterSession {
  readonly adapterId: string
  readonly appToken: string
  readonly botToken: string
}

export interface SlackFileRef {
  readonly id: string
  readonly name: string
  readonly mimetype: string
  readonly size: number
}

export interface SlackEnvelope {
  readonly eventType: string
  readonly isDirectMessage: boolean
  readonly teamId: string
  readonly conversationId: string
  readonly messageTs: string
  readonly threadTs: string | null
  readonly mentionedUserIds: readonly string[]
  readonly senderSlackUserId: string | null
  readonly senderKind: SlackSenderKind
  readonly text: string | null
  readonly files: readonly SlackFileRef[]
}

export type SlackSenderKind = "human" | "bot" | "unknown"

export type IngressResult =
  | { readonly kind: "accepted" }
  | { readonly kind: "rejected"; readonly reason?: string }
  | { readonly kind: "claimed" }
  | { readonly kind: "transferred" }
  | { readonly kind: "ignored" }
  | { readonly kind: "backpressured"; readonly reason: string }
  | { readonly kind: string; readonly reason?: string }

export interface Delivery {
  readonly id: string
  readonly conversationId: string
  readonly threadTs: string | null
  readonly payloadJson: string
}

export interface ProviderMessageIdentity {
  readonly conversationId: string
  readonly messageTs: string
}

export interface DeliveryAck {
  readonly id: string
  readonly outcome: "delivered" | "uncertain" | "retry"
  readonly reason?: string
  readonly providerMessageIdentity?: ProviderMessageIdentity
}

export interface AdapterTransport {
  discoverConnections(signal: AbortSignal): Promise<readonly SlackConnectionRef[]>
  lease(ref: SlackConnectionRef, adapterId: string, signal: AbortSignal): Promise<AdapterSession>
  ingress(ref: SlackConnectionRef, envelope: SlackEnvelope, signal: AbortSignal): Promise<IngressResult>
  claimDelivery(ref: SlackConnectionRef, adapterId: string, signal: AbortSignal): Promise<Delivery | null>
  claimUncertainDelivery?(ref: SlackConnectionRef, adapterId: string, signal: AbortSignal): Promise<Delivery | null>
  ackDelivery(ref: SlackConnectionRef, ack: DeliveryAck, signal: AbortSignal): Promise<void>
}

export interface SocketEvent {
  readonly body: unknown
  readonly ack: () => Promise<void> | void
}

export interface SocketClient {
  on(event: "slack_event", handler: (event: SocketEvent) => Promise<void>): void
  start(): Promise<void>
  disconnect?(): Promise<void>
}

export interface SlackWebClient {
  chat: {
    postMessage(input: { channel: string; text: string; thread_ts?: string; client_msg_id?: string }): Promise<{ ok?: boolean; error?: string; ts?: string }>
    update?(input: { channel: string; ts: string; text: string }): Promise<{ ok?: boolean; error?: string; ts?: string }>
  }
  reactions?: {
    add(input: { channel: string; name: string; timestamp: string }): Promise<{ ok?: boolean; error?: string }>
    remove(input: { channel: string; name: string; timestamp: string }): Promise<{ ok?: boolean; error?: string }>
    get?(input: { channel: string; timestamp: string; full?: boolean }): Promise<{ ok?: boolean; error?: string; message?: { reactions?: readonly { name?: string; users?: readonly string[] }[] } }>
  }
  conversations?: {
    history(input: { channel: string; latest?: string; oldest?: string; inclusive?: boolean; limit?: number }): Promise<{ ok?: boolean; error?: string; messages?: readonly { ts?: string; client_msg_id?: string; text?: string; thread_ts?: string }[] }>
  }
}

export type SocketClientFactory = (appToken: string, ref: SlackConnectionRef) => SocketClient
export type WebClientFactory = (botToken: string, ref: SlackConnectionRef) => SlackWebClient
