export type SlackDeliveryOwnerKind = "connection" | "manager"

export interface SlackConnectionRef {
  readonly ownerKind?: "connection"
  readonly projectId: string
  readonly connectionId: string
}

export interface SlackManagerRef {
  readonly ownerKind: "manager"
  readonly enrollmentId: string
  readonly workspaceTeamId: string
}

export type SlackAdapterTarget = SlackConnectionRef | SlackManagerRef

export type AdapterLease = ValidationLease | RuntimeLease

export interface ValidationLease {
  readonly kind: "validation"
  readonly leaseId: string
  readonly appToken: string
}

export interface RuntimeLease {
  readonly kind: "runtime"
  readonly leaseId: string
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
  readonly apiAppId: string
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

export interface SlackInteractionEnvelope {
  readonly eventType: "block_actions"
  readonly apiAppId: string
  readonly interactionId: string
  readonly teamId: string
  readonly conversationId: string
  readonly messageTs: string
  readonly threadTs: string | null
  readonly actorSlackUserId: string
  readonly actionId: string
  readonly actionValue: string
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

export interface InteractionResult {
  readonly state: string
}

export interface Delivery {
  readonly id: string
  readonly ownerKind?: SlackDeliveryOwnerKind
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
  readonly adapterId?: string
  readonly reason?: string
  readonly providerMessageIdentity?: ProviderMessageIdentity
}

export interface AdapterTransport {
  discover(signal: AbortSignal): Promise<readonly SlackAdapterTarget[]>
  acquireLease(ref: SlackAdapterTarget, adapterId: string, signal: AbortSignal): Promise<AdapterLease | null>
  renewLease(ref: SlackAdapterTarget, leaseId: string, adapterId: string, signal: AbortSignal): Promise<AdapterLease | null>
  reportHello(ref: SlackAdapterTarget, leaseId: string, appId: string, signal: AbortSignal): Promise<void>
  ingress(ref: SlackAdapterTarget, leaseId: string, envelope: SlackEnvelope, signal: AbortSignal): Promise<IngressResult>
  interaction(ref: SlackAdapterTarget, leaseId: string, envelope: SlackInteractionEnvelope, signal: AbortSignal): Promise<InteractionResult>
  claimDelivery(ref: SlackAdapterTarget, leaseId: string, adapterId: string, signal: AbortSignal): Promise<Delivery | null>
  claimUncertainDelivery?(ref: SlackAdapterTarget, leaseId: string, adapterId: string, signal: AbortSignal): Promise<Delivery | null>
  ackDelivery(ref: SlackAdapterTarget, leaseId: string, ack: DeliveryAck, signal: AbortSignal): Promise<void>
}

export interface SocketEvent {
  readonly body: unknown
  readonly ack: () => Promise<void> | void
}

export interface SocketHello {
  readonly appId: string
}

export interface SocketClient {
  on(event: "slack_event", handler: (event: SocketEvent) => Promise<void>): void
  onState?(event: "connecting" | "connected" | "reconnecting" | "disconnected" | "error", handler: (error?: unknown) => void): void
  start(): Promise<SocketHello>
  disconnect?(): Promise<void>
}

export interface SlackWebClient {
  chat: {
    postMessage(input: { channel: string; text: string; thread_ts?: string; client_msg_id?: string; blocks?: readonly SlackBlock[] }): Promise<{ ok?: boolean; error?: string; ts?: string }>
    update?(input: { channel: string; ts: string; text: string; blocks?: readonly SlackBlock[] }): Promise<{ ok?: boolean; error?: string; ts?: string }>
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

export type SlackBlock = Record<string, unknown>

export type SocketClientFactory = (appToken: string, ref: SlackAdapterTarget) => SocketClient
export type WebClientFactory = (botToken: string, ref: SlackAdapterTarget) => SlackWebClient
