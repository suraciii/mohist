import type { AdapterTransport, AdapterSession, Delivery, DeliveryAck, IngressResult, InteractionResult, SlackConnectionRef, SlackEnvelope, SlackInteractionEnvelope } from "./types.js"

export interface HttpTransportOptions {
  readonly serverUrl: string
  readonly operatorToken: string
  readonly fetch?: typeof fetch
}

export class HttpAdapterTransport implements AdapterTransport {
  private readonly request: typeof fetch
  private readonly baseUrl: string

  constructor(private readonly options: HttpTransportOptions) {
    this.request = options.fetch ?? fetch
    this.baseUrl = options.serverUrl.replace(/\/$/, "")
  }

  discoverConnections(signal: AbortSignal) {
    return this.get<SlackConnectionRef[]>("/api/slack-connections/adapter", signal)
  }

  lease(ref: SlackConnectionRef, adapterId: string, signal: AbortSignal) {
    return this.post<AdapterSession>(ref, "adapter-session", { adapterId }, signal)
  }

  ingress(ref: SlackConnectionRef, envelope: SlackEnvelope, signal: AbortSignal) {
    return this.post<IngressResult>(ref, "ingress", envelope, signal)
  }

  interaction(ref: SlackConnectionRef, envelope: SlackInteractionEnvelope, signal: AbortSignal) {
    return this.post<InteractionResult>(ref, "interactions", envelope, signal)
  }

  claimDelivery(ref: SlackConnectionRef, adapterId: string, signal: AbortSignal) {
    return this.post<Delivery | null>(ref, "deliveries/claim", { adapterId }, signal)
  }

  claimUncertainDelivery(ref: SlackConnectionRef, adapterId: string, signal: AbortSignal) {
    return this.post<Delivery | null>(ref, "deliveries/claim-uncertain", { adapterId }, signal)
  }

  async ackDelivery(ref: SlackConnectionRef, ack: DeliveryAck, signal: AbortSignal) {
    await this.post(ref, "deliveries/ack", ack, signal)
  }

  private async post<T>(ref: SlackConnectionRef, route: string, body: unknown, signal: AbortSignal): Promise<T> {
    const response = await this.request(`${this.baseUrl}/api/projects/${encodeURIComponent(ref.projectId)}/slack-connections/${encodeURIComponent(ref.connectionId)}/${route}`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-mohist-operator-token": this.options.operatorToken,
      },
      body: JSON.stringify(body),
      signal,
    })
    return await this.read<T>(response)
  }

  private async get<T>(path: string, signal: AbortSignal): Promise<T> {
    const response = await this.request(`${this.baseUrl}${path}`, {
      headers: { "x-mohist-operator-token": this.options.operatorToken },
      signal,
    })
    return await this.read<T>(response)
  }

  private async read<T>(response: Response): Promise<T> {
    const text = await response.text()
    let payload: unknown = null
    if (text) payload = JSON.parse(text) as unknown
    if (!response.ok) throw new Error(`Slack adapter request failed: ${response.status} ${text}`)
    if (!isRecord(payload) || payload.success !== true) throw new Error("Slack adapter returned an unsuccessful response")
    return payload.data as T
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
}
