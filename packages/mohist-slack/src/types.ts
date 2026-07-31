export interface SlackConnectionRef {
  readonly projectId: string
  readonly connectionId: string
}

export interface AdapterSession {
  readonly adapterId: string
  readonly appToken: string
  readonly botToken: string
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
}

export type SlackSenderKind = "human" | "bot" | "unknown"

export interface IngressResult {
  readonly kind: string
  readonly reason?: string
}

export interface Delivery {
  readonly id: string
  readonly conversationId: string
  readonly threadTs: string | null
  readonly payloadJson: string
}

export interface DeliveryAck {
  readonly id: string
  readonly outcome: "delivered" | "uncertain" | "retry"
  readonly reason?: string
}

export interface AdapterTransport {
  discoverConnections(signal: AbortSignal): Promise<readonly SlackConnectionRef[]>
  lease(ref: SlackConnectionRef, adapterId: string, signal: AbortSignal): Promise<AdapterSession>
  ingress(ref: SlackConnectionRef, envelope: SlackEnvelope, signal: AbortSignal): Promise<IngressResult>
  claimDelivery(ref: SlackConnectionRef, adapterId: string, signal: AbortSignal): Promise<Delivery | null>
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
    postMessage(input: { channel: string; text: string; thread_ts?: string }): Promise<{ ok?: boolean; error?: string }>
  }
}

export type SocketClientFactory = (appToken: string, ref: SlackConnectionRef) => SocketClient
export type WebClientFactory = (botToken: string, ref: SlackConnectionRef) => SlackWebClient
