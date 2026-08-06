import type { AdapterLease, AdapterTransport, Delivery, DeliveryAck, IngressResult, InteractionResult, LeaseRenewal, SlackAdapterTarget, SlackConnectionRef, SlackEnvelope, SlackHelloOutcome, SlackInteractionEnvelope, SlackLeaseKind, SlackManagerRef } from "./types.js"

export interface HttpTransportOptions {
  readonly serverUrl: string
  readonly operatorToken: string
  readonly operatorId: string
  readonly fetch?: typeof fetch
}

const LEASE_ROUTES = "/api/slack-adapter/leases"
const OPERATOR_TOKEN_HEADER = "x-mohist-operator-token"
const OPERATOR_ID_HEADER = "x-mohist-operator-id"

/**
 * Thrown when a runtime-lease-gated request (ingress, interaction, claim,
 * ack) is rejected because the presented lease is stale, superseded or
 * expired. The adapter reacts by dropping the runtime so the next discovery
 * cycle re-acquires a fresh lease.
 */
export class LeaseStaleError extends Error {
  constructor() {
    super("Slack adapter runtime lease is stale or expired")
  }
}

export class HttpAdapterTransport implements AdapterTransport {
  private readonly request: typeof fetch
  private readonly baseUrl: string

  constructor(private readonly options: HttpTransportOptions) {
    this.request = options.fetch ?? fetch
    this.baseUrl = loopbackBaseUrl(options.serverUrl)
  }

  async discover(signal: AbortSignal) {
    const data = await this.get<unknown>(`${LEASE_ROUTES}/targets`, signal)
    if (!Array.isArray(data)) throw new Error("Slack adapter discovery returned an invalid response")
    return data.map(discoveryTarget)
  }

  async acquireLease(ref: SlackAdapterTarget, kind: SlackLeaseKind, adapterId: string, signal: AbortSignal): Promise<AdapterLease | null> {
    const data = await this.postOrNull<unknown>(
      `${LEASE_ROUTES}/acquire`,
      { kind, target: targetBody(ref), adapterId },
      "lease_not_acquirable",
      signal,
    )
    return data === null ? null : leaseFromData(kind, data)
  }

  async renewLease(ref: SlackAdapterTarget, leaseId: string, adapterId: string, signal: AbortSignal): Promise<LeaseRenewal | null> {
    const data = await this.postOrNull<unknown>(
      `${LEASE_ROUTES}/renew`,
      { target: targetBody(ref), leaseId, adapterId },
      "lease_stale_or_expired",
      signal,
    )
    return data === null ? null : renewalFromData(data)
  }

  async reportHello(ref: SlackAdapterTarget, leaseId: string, appId: string, signal: AbortSignal): Promise<SlackHelloOutcome> {
    const payload = await this.postEnvelope(`${LEASE_ROUTES}/hello`, { target: targetBody(ref), leaseId, appId }, signal)
    if (payload.ok) {
      const outcome = isRecord(payload.data) ? stringValue(payload.data.outcome) : null
      return outcome === "verified" ? "verified" : "lease_stale_or_expired"
    }
    if (payload.code === "app_id_mismatch") return "app_id_mismatch"
    if (payload.code === "lease_stale_or_expired") return "lease_stale_or_expired"
    throw this.failure(payload)
  }

  async ingress(ref: SlackAdapterTarget, envelope: SlackEnvelope, leaseId: string, adapterId: string, signal: AbortSignal): Promise<IngressResult> {
    if (isManagerTarget(ref))
      return await this.post<IngressResult>("/api/slack-manager/ingress", { ...managerIngressBody(ref, envelope), leaseId, adapterId }, signal)
    return await this.post<IngressResult>(`${connectionRoute(ref)}/ingress`, { ...envelope, leaseId, adapterId }, signal)
  }

  async interaction(ref: SlackAdapterTarget, envelope: SlackInteractionEnvelope, leaseId: string, adapterId: string, signal: AbortSignal): Promise<InteractionResult> {
    if (isManagerTarget(ref))
      throw new Error("Slack Manager targets do not expose interactions")
    return await this.post<InteractionResult>(`${connectionRoute(ref)}/interactions`, { ...envelope, leaseId, adapterId }, signal)
  }

  async claimDelivery(ref: SlackAdapterTarget, leaseId: string, adapterId: string, signal: AbortSignal): Promise<Delivery | null> {
    const data = await this.postOrNull<unknown>(deliveryRoute(ref, "claim"), { leaseId, adapterId }, undefined, signal)
    return deliveryFromData(data)
  }

  async claimUncertainDelivery(ref: SlackAdapterTarget, leaseId: string, adapterId: string, signal: AbortSignal): Promise<Delivery | null> {
    const data = await this.postOrNull<unknown>(deliveryRoute(ref, "claim-uncertain"), { leaseId, adapterId }, undefined, signal)
    return deliveryFromData(data)
  }

  async ackDelivery(ref: SlackAdapterTarget, ack: DeliveryAck, leaseId: string, signal: AbortSignal): Promise<void> {
    await this.post<unknown>(deliveryRoute(ref, "ack"), { ...ack, leaseId }, signal)
  }

  private operatorHeaders(): Record<string, string> {
    return {
      [OPERATOR_TOKEN_HEADER]: this.options.operatorToken,
      [OPERATOR_ID_HEADER]: this.options.operatorId,
    }
  }

  private async get<T>(path: string, signal: AbortSignal): Promise<T> {
    const response = await this.request(`${this.baseUrl}${path}`, {
      headers: this.operatorHeaders(),
      signal,
    })
    const payload = await this.envelope(response)
    if (!payload.ok) throw this.failure(payload)
    return payload.data as T
  }

  private async post<T>(path: string, body: unknown, signal: AbortSignal): Promise<T> {
    const payload = await this.postEnvelope(path, body, signal)
    if (!payload.ok) throw this.failure(payload)
    return payload.data as T
  }

  private async postOrNull<T>(path: string, body: unknown, nullCode: string | undefined, signal: AbortSignal): Promise<T | null> {
    const payload = await this.postEnvelope(path, body, signal)
    if (!payload.ok) {
      if (nullCode !== undefined && payload.code === nullCode) return null
      throw this.failure(payload)
    }
    return payload.data === null || payload.data === undefined ? null : (payload.data as T)
  }

  private async postEnvelope(path: string, body: unknown, signal: AbortSignal) {
    const response = await this.request(`${this.baseUrl}${path}`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        ...this.operatorHeaders(),
      },
      body: JSON.stringify(body),
      signal,
    })
    return await this.envelope(response)
  }

  private async envelope(response: Response): Promise<{ ok: boolean; status: number; code?: string; data?: unknown }> {
    const text = await response.text()
    let parsed: unknown = null
    if (text) {
      try {
        parsed = JSON.parse(text) as unknown
      } catch {
        parsed = null
      }
    }
    if (!isRecord(parsed)) throw new Error(`Slack adapter returned an invalid response (${response.status})`)
    return {
      ok: parsed.success === true,
      status: response.status,
      code: typeof parsed.code === "string" ? parsed.code : undefined,
      data: parsed.data,
    }
  }

  private failure(payload: { status: number; code?: string }): Error {
    if (payload.code === "lease_stale_or_expired") return new LeaseStaleError()
    return new Error(`Slack adapter request failed: ${payload.status}${payload.code ? ` (${payload.code})` : ""}`)
  }
}

function targetBody(ref: SlackAdapterTarget): Record<string, unknown> {
  return isManagerTarget(ref)
    ? { kind: "manager", enrollmentId: ref.enrollmentId, workspaceTeamId: ref.workspaceTeamId }
    : { kind: "connection", projectId: ref.projectId, connectionId: ref.connectionId }
}

function discoveryTarget(value: unknown): SlackAdapterTarget {
  if (!isRecord(value)) throw new Error("Slack adapter discovery returned an invalid target")
  if (value.kind === "manager") {
    const enrollmentId = stringValue(value.enrollmentId)
    const workspaceTeamId = stringValue(value.workspaceTeamId)
    if (!enrollmentId || !workspaceTeamId) throw new Error("Slack adapter discovery returned an invalid Manager target")
    return { kind: "manager", enrollmentId, workspaceTeamId }
  }
  if (value.kind === "connection") {
    const projectId = stringValue(value.projectId)
    const connectionId = stringValue(value.connectionId)
    if (!projectId || !connectionId) throw new Error("Slack adapter discovery returned an invalid Connection target")
    return { projectId, connectionId }
  }
  throw new Error("Slack adapter discovery returned an invalid target kind")
}

function leaseFromData(kind: SlackLeaseKind, value: unknown): AdapterLease {
  if (!isRecord(value)) throw new Error("Slack adapter returned an invalid lease response")
  const leaseId = stringValue(value.leaseId)
  const appToken = stringValue(value.appToken)
  const generation = numberValue(value.generation)
  const expiresAt = stringValue(value.expiresAt)
  if (!leaseId || !appToken || generation === null || !expiresAt)
    throw new Error("Slack adapter returned an invalid lease response")
  if (kind === "validation") {
    const expectedAppId = stringValue(value.expectedAppId)
    if (!expectedAppId) throw new Error("Slack adapter returned an invalid validation lease response")
    return { kind: "validation", leaseId, generation, expiresAt, expectedAppId, appToken }
  }
  const botToken = stringValue(value.botToken)
  if (!botToken) throw new Error("Slack adapter returned an invalid runtime lease response")
  return { kind: "runtime", leaseId, generation, expiresAt, appToken, botToken }
}

function renewalFromData(value: unknown): LeaseRenewal {
  if (!isRecord(value)) throw new Error("Slack adapter returned an invalid lease renewal response")
  const leaseId = stringValue(value.leaseId)
  const kind = stringValue(value.kind)
  const generation = numberValue(value.generation)
  const expiresAt = stringValue(value.expiresAt)
  if (!leaseId || !kind || generation === null || !expiresAt)
    throw new Error("Slack adapter returned an invalid lease renewal response")
  return { leaseId, kind: kind === "validation" ? "validation" : "runtime", generation, expiresAt }
}

function deliveryFromData(value: unknown): Delivery | null {
  if (value === null || value === undefined) return null
  if (!isRecord(value)) throw new Error("Slack adapter returned an invalid delivery")
  const id = stringValue(value.id)
  const conversationId = stringValue(value.conversationId)
  const payloadJson = stringValue(value.payloadJson)
  if (!id || !conversationId || !payloadJson) throw new Error("Slack adapter returned an invalid delivery")
  return {
    id,
    ownerKind: value.ownerKind === "manager" || value.ownerKind === "connection" ? value.ownerKind : undefined,
    conversationId,
    threadTs: stringValue(value.threadTs),
    payloadJson,
  }
}

function managerIngressBody(ref: SlackManagerRef, envelope: SlackEnvelope): Record<string, unknown> {
  return {
    appId: envelope.apiAppId,
    workspaceTeamId: ref.workspaceTeamId,
    conversationId: envelope.conversationId,
    messageTs: envelope.messageTs,
    senderSlackUserId: envelope.senderSlackUserId,
    text: envelope.text,
    isDirectMessage: envelope.isDirectMessage,
    threadTs: envelope.threadTs,
  }
}

function connectionRoute(ref: SlackConnectionRef): string {
  return `/api/projects/${encodeURIComponent(ref.projectId)}/slack-connections/${encodeURIComponent(ref.connectionId)}`
}

function deliveryRoute(ref: SlackAdapterTarget, action: string): string {
  return isManagerTarget(ref)
    ? `/api/slack-manager/adapter/${encodeURIComponent(ref.enrollmentId)}/deliveries/${action}`
    : `${connectionRoute(ref)}/deliveries/${action}`
}

function isManagerTarget(value: SlackAdapterTarget): value is SlackManagerRef {
  return value.kind === "manager"
}

function numberValue(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null
}

function stringValue(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
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
