import type { AdapterTransport, AdapterSession, Delivery, DeliveryAck, IngressResult, InteractionResult, SlackAdapterTarget, SlackConnectionRef, SlackEnvelope, SlackInteractionEnvelope, SlackManagerRef } from "./types.js"

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
    return Promise.all([
      this.get<SlackConnectionRef[]>("/api/slack-connections/adapter", signal),
      this.get<SlackManagerRef[]>("/api/slack-manager/adapter", signal),
    ]).then(([connections, managers]) => [...connections, ...managers])
  }

  lease(ref: SlackAdapterTarget, adapterId: string, signal: AbortSignal) {
    return isManagerTarget(ref)
      ? this.postManager<AdapterSession>(ref, "session", { adapterId }, signal)
      : this.postConnection<AdapterSession>(ref, "adapter-session", { adapterId }, signal)
  }

  ingress(ref: SlackAdapterTarget, envelope: SlackEnvelope, signal: AbortSignal) {
    if (isManagerTarget(ref)) throw new Error("Manager targets do not accept Connection ingress")
    return this.postConnection<IngressResult>(ref, "ingress", envelope, signal)
  }

  interaction(ref: SlackAdapterTarget, envelope: SlackInteractionEnvelope, signal: AbortSignal) {
    if (isManagerTarget(ref)) throw new Error("Manager targets do not accept Connection interactions")
    return this.postConnection<InteractionResult>(ref, "interactions", envelope, signal)
  }

  claimDelivery(ref: SlackAdapterTarget, adapterId: string, signal: AbortSignal) {
    return isManagerTarget(ref)
      ? this.postManager<Delivery | null>(ref, "deliveries/claim", { adapterId }, signal)
      : this.postConnection<Delivery | null>(ref, "deliveries/claim", { adapterId }, signal)
  }

  claimUncertainDelivery(ref: SlackAdapterTarget, adapterId: string, signal: AbortSignal) {
    return isManagerTarget(ref)
      ? this.postManager<Delivery | null>(ref, "deliveries/claim-uncertain", { adapterId }, signal)
      : this.postConnection<Delivery | null>(ref, "deliveries/claim-uncertain", { adapterId }, signal)
  }

  async ackDelivery(ref: SlackAdapterTarget, ack: DeliveryAck, signal: AbortSignal) {
    if (isManagerTarget(ref)) {
      await this.postManager(ref, "deliveries/ack", ack, signal)
      return
    }
    await this.postConnection(ref, "deliveries/ack", ack, signal)
  }

  private async postConnection<T>(ref: SlackConnectionRef, route: string, body: unknown, signal: AbortSignal): Promise<T> {
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

  private async postManager<T>(ref: SlackManagerRef, route: string, body: unknown, signal: AbortSignal): Promise<T> {
    const response = await this.request(`${this.baseUrl}/api/slack-manager/adapter/${encodeURIComponent(ref.enrollmentId)}/${route}`, {
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

function isManagerTarget(value: SlackAdapterTarget): value is SlackManagerRef {
  return value.ownerKind === "manager"
}
