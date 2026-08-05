import type { AdapterLease, AdapterTransport, Delivery, DeliveryAck, IngressResult, InteractionResult, SlackAdapterTarget, SlackConnectionRef, SlackEnvelope, SlackInteractionEnvelope, SlackManagerRef } from "./types.js"

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
    this.baseUrl = loopbackBaseUrl(options.serverUrl)
  }

  discover(signal: AbortSignal) {
    return Promise.all([
      this.get<SlackConnectionRef[]>("/api/slack-connections/adapter", signal),
      this.get<SlackManagerRef[]>("/api/slack-manager/adapter", signal),
    ]).then(([connections, managers]) => [
      ...connections.map(connectionTarget),
      ...managers.map(managerTarget),
    ])
  }

  acquireLease(ref: SlackAdapterTarget, adapterId: string, signal: AbortSignal) {
    return this.postTarget<AdapterLease | null>(ref, "leases/acquire", { adapterId }, signal)
  }

  renewLease(ref: SlackAdapterTarget, leaseId: string, adapterId: string, signal: AbortSignal) {
    return this.postTarget<AdapterLease | null>(ref, `leases/${encodeURIComponent(leaseId)}/renew`, { adapterId }, signal)
  }

  async reportHello(ref: SlackAdapterTarget, leaseId: string, appId: string, signal: AbortSignal) {
    await this.postTarget(ref, `leases/${encodeURIComponent(leaseId)}/hello`, { appId }, signal)
  }

  ingress(ref: SlackAdapterTarget, leaseId: string, envelope: SlackEnvelope, signal: AbortSignal) {
    return this.postTarget<IngressResult>(ref, `leases/${encodeURIComponent(leaseId)}/ingress`, envelope, signal)
  }

  interaction(ref: SlackAdapterTarget, leaseId: string, envelope: SlackInteractionEnvelope, signal: AbortSignal) {
    return this.postTarget<InteractionResult>(ref, `leases/${encodeURIComponent(leaseId)}/interactions`, envelope, signal)
  }

  claimDelivery(ref: SlackAdapterTarget, leaseId: string, adapterId: string, signal: AbortSignal) {
    return this.postTarget<Delivery | null>(ref, `leases/${encodeURIComponent(leaseId)}/deliveries/claim`, { adapterId }, signal)
  }

  claimUncertainDelivery(ref: SlackAdapterTarget, leaseId: string, adapterId: string, signal: AbortSignal) {
    return this.postTarget<Delivery | null>(ref, `leases/${encodeURIComponent(leaseId)}/deliveries/claim-uncertain`, { adapterId }, signal)
  }

  async ackDelivery(ref: SlackAdapterTarget, leaseId: string, ack: DeliveryAck, signal: AbortSignal) {
    await this.postTarget(ref, `leases/${encodeURIComponent(leaseId)}/deliveries/ack`, ack, signal)
  }

  private postTarget<T>(ref: SlackAdapterTarget, route: string, body: unknown, signal: AbortSignal): Promise<T> {
    return isManagerTarget(ref)
      ? this.postManager(ref, route, body, signal)
      : this.postConnection(ref, route, body, signal)
  }

  private async postConnection<T>(ref: SlackConnectionRef, route: string, body: unknown, signal: AbortSignal): Promise<T> {
    const response = await this.request(`${this.baseUrl}/api/projects/${encodeURIComponent(ref.projectId)}/slack-connections/${encodeURIComponent(ref.connectionId)}/adapter/${route}`, {
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
    if (!response.ok) throw new Error(`Slack adapter request failed: ${response.status}`)
    let payload: unknown = null
    if (text) payload = JSON.parse(text) as unknown
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

function connectionTarget(value: SlackConnectionRef): SlackConnectionRef {
  if (!isRecord(value) || typeof value.projectId !== "string" || typeof value.connectionId !== "string")
    throw new Error("Slack adapter discovery returned an invalid Connection target")
  return { projectId: value.projectId, connectionId: value.connectionId }
}

function managerTarget(value: SlackManagerRef): SlackManagerRef {
  if (!isRecord(value) || value.ownerKind !== "manager" || typeof value.enrollmentId !== "string" || typeof value.workspaceTeamId !== "string")
    throw new Error("Slack adapter discovery returned an invalid Manager target")
  return {
    ownerKind: "manager",
    enrollmentId: value.enrollmentId,
    workspaceTeamId: value.workspaceTeamId,
  }
}

function loopbackBaseUrl(value: string): string {
  const url = new URL(value)
  if ((url.protocol !== "http:" && url.protocol !== "https:") || !isLoopbackHost(url.hostname))
    throw new Error("Slack adapter Server URL must be loopback")
  return url.toString().replace(/\/$/, "")
}

function isLoopbackHost(hostname: string): boolean {
  if (hostname === "localhost" || hostname === "[::1]") return true
  const octets = hostname.split(".")
  return octets.length === 4
    && octets[0] === "127"
    && octets.every((octet) => /^\d+$/.test(octet) && Number(octet) <= 255)
}
