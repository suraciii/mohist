import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { SlackAdapter, normalizeSlackInteraction, normalizeSocketEvent } from "./adapter.js"
import { LeaseStaleError } from "./transport.js"
import { setSlackLoggerForTest, type SlackLogFields, type SlackLogger } from "./logger.js"
import type { AdapterLease, AdapterTransport, Delivery, IngressResult, LeaseRenewal, RuntimeLease, SlackAdapterTarget, SlackConnectionRef, SlackEnvelope, SlackHelloOutcome, SlackInteractionEnvelope, SlackLeaseKind, SlackManagerRef, SlackWebClient, SocketClient, SocketEvent } from "./types.js"

class FakeSocket implements SocketClient {
  private handler?: (event: SocketEvent) => Promise<void>
  started = false
  starts = 0
  disconnected = false
  acknowledged = false
  disconnectError?: Error
  disconnectGate?: Promise<void>
  disconnectStarted?: () => void
  startGate?: Promise<void>
  startStarted?: () => void

  on(_event: "slack_event", handler: (event: SocketEvent) => Promise<void>) {
    this.handler = handler
  }

  async start() {
    this.started = true
    this.starts += 1
    this.startStarted?.()
    await this.startGate
    return { appId: "A1" }
  }

  async emit(body: unknown) {
    this.acknowledged = false
    await this.handler?.({ body, ack: () => { this.acknowledged = true } })
    return this.acknowledged
  }

  async disconnect() {
    this.disconnected = true
    this.disconnectStarted?.()
    await this.disconnectGate
    if (this.disconnectError) throw this.disconnectError
  }
}

class RecordingLogger implements SlackLogger {
  readonly entries: Array<{ level: "info" | "error"; message: string; fields?: SlackLogFields }> = []

  info(message: string, fields?: SlackLogFields): void {
    this.entries.push({ level: "info", message, fields })
  }

  error(message: string, fields?: SlackLogFields): void {
    this.entries.push({ level: "error", message, fields })
  }

  child(): SlackLogger {
    return this
  }

  async flush(): Promise<void> {}
}

class FakeTransport implements AdapterTransport {
  readonly leases: SlackAdapterTarget[] = []
  readonly envelopes: SlackEnvelope[] = []
  readonly interactions: SlackInteractionEnvelope[] = []
  readonly acks: Array<{ ref: SlackAdapterTarget; id: string; outcome: string }> = []
  readonly hellos: Array<{ ref: SlackAdapterTarget; leaseId: string; appId: string }> = []
  readonly deliveries: Delivery[] = [{ id: "delivery-1", conversationId: "D1", threadTs: null, payloadJson: JSON.stringify({ text: "accepted" }) }]
  readonly uncertainDeliveries: Delivery[] = []
  connections: SlackAdapterTarget[] = []
  nextLeases: Array<AdapterLease | null> = []
  nextRenewals: Array<LeaseRenewal | null> = []
  nextIngressResults: IngressResult[] = []
  ingressError?: Error
  ingressGate?: Promise<void>
  ingressStarted?: () => void
  interactionError?: Error
  interactionGate?: Promise<void>
  interactionStarted?: () => void
  uncertainGate?: Promise<void>
  uncertainStarted?: () => void
  claimDeliveryCalls = 0
  leaseError?: Error

  async discover(): Promise<readonly SlackAdapterTarget[]> {
    return this.connections
  }

  async acquireLease(ref: SlackAdapterTarget, kind: SlackLeaseKind): Promise<AdapterLease | null> {
    if (this.leaseError) throw this.leaseError
    const lease = this.nextLeases.length > 0
      ? this.nextLeases.shift()!
      : kind === "validation" ? null : runtimeLease(ref)
    if (lease) this.leases.push(ref)
    return lease
  }

  async renewLease(ref: SlackAdapterTarget, leaseId: string): Promise<LeaseRenewal | null> {
    if (this.leaseError) throw this.leaseError
    return this.nextRenewals.length > 0
      ? this.nextRenewals.shift()!
      : { leaseId, kind: "runtime", generation: 1, expiresAt: "2026-01-01T00:05:00Z" }
  }

  async reportHello(ref: SlackAdapterTarget, leaseId: string, appId: string): Promise<SlackHelloOutcome> {
    this.hellos.push({ ref, leaseId, appId })
    return "verified"
  }

  async ingress(_ref: SlackAdapterTarget, envelope: SlackEnvelope): Promise<IngressResult> {
    this.envelopes.push(envelope)
    this.ingressStarted?.()
    await this.ingressGate
    if (this.ingressError) throw this.ingressError
    const queued = this.nextIngressResults.shift()
    return queued ?? { kind: "accepted" }
  }

  async interaction(_ref: SlackAdapterTarget, envelope: SlackInteractionEnvelope) {
    this.interactions.push(envelope)
    this.interactionStarted?.()
    await this.interactionGate
    if (this.interactionError) throw this.interactionError
    return { state: "stop_requested" }
  }

  async claimDelivery(): Promise<Delivery | null> {
    this.claimDeliveryCalls += 1
    return this.deliveries.shift() ?? null
  }

  async claimUncertainDelivery(): Promise<Delivery | null> {
    this.uncertainStarted?.()
    await this.uncertainGate
    return this.uncertainDeliveries.shift() ?? null
  }

  async ackDelivery(ref: SlackAdapterTarget, ack: DeliveryAck) {
    this.acks.push({
      ref,
      id: ack.id,
      outcome: ack.outcome,
      ...(ack.providerMessageIdentity ? { providerMessageIdentity: ack.providerMessageIdentity } : {}),
    })
  }
}

function runtimeLease(ref: SlackAdapterTarget): RuntimeLease {
  return {
    kind: "runtime",
    leaseId: `lease-${ref.kind === "manager" ? ref.enrollmentId : ref.connectionId}`,
    generation: 1,
    expiresAt: "2026-01-01T00:05:00Z",
    appToken: `xapp-${ref.kind === "manager" ? ref.enrollmentId : ref.connectionId}`,
    botToken: `xoxb-${ref.kind === "manager" ? ref.enrollmentId : ref.connectionId}`,
  }
}

class FakeWeb implements SlackWebClient {
  readonly posted: Array<{ channel: string; text: string; thread_ts?: string; client_msg_id?: string; blocks?: readonly Record<string, unknown>[] }> = []
  readonly updated: Array<{ channel: string; ts: string; text: string; blocks?: readonly Record<string, unknown>[] }> = []
  readonly uploaded: Array<Record<string, unknown>> = []
  nextResponses: Array<{ ok?: boolean; error?: string }> = []
  nextUploadResponses: Array<{ ok?: boolean; error?: string; files?: readonly { ok?: boolean; error?: string; files?: readonly { id?: string; shares?: { public?: Record<string, readonly { ts?: string }[]>; private?: Record<string, readonly { ts?: string }[]> } }[] }[] }> = []
  readonly chat = {
    postMessage: async (input: { channel: string; text: string; thread_ts?: string; client_msg_id?: string; blocks?: readonly Record<string, unknown>[] }) => {
      this.posted.push(input)
      const next = this.nextResponses.shift()
      return next ?? { ok: true }
    },
    update: async (input: { channel: string; ts: string; text: string; blocks?: readonly Record<string, unknown>[] }) => {
      this.updated.push(input)
      return { ok: true, ts: input.ts }
    },
  }
  readonly filesUploadV2 = async (input: { channel_id: string; filename?: string; file: Buffer; initial_comment?: string; alt_text?: string } | { channels: string; thread_ts: string; filename?: string; file: Buffer; initial_comment?: string; alt_text?: string }) => {
    this.uploaded.push({ ...input })
    const next = this.nextUploadResponses.shift()
    return next ?? { ok: true }
  }
}

describe("mohist-slack adapter", () => {
  let logger: RecordingLogger
  let restoreLogger: () => void

  beforeEach(() => {
    logger = new RecordingLogger()
    restoreLogger = setSlackLoggerForTest(logger)
  })

  afterEach(() => {
    vi.useRealTimers()
    restoreLogger()
  })

  it("logs a failed target connection with its identity and redacted credential", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }]
    transport.leaseError = new Error("Slack rejected xapp-secret-value")
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => new FakeWeb(),
    })

    await adapter.start(new AbortController().signal)

    expect(logger.entries).toContainEqual({
      level: "error",
      message: "target connection failed",
      fields: {
        target: "connection:p1:c1",
        reason: "Slack rejected <redacted>",
      },
    })
    expect(JSON.stringify(logger.entries)).not.toContain("xapp-secret-value")
    await adapter.stop()
  })

  it("contains and redacts a Socket disconnect failure", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }]
    const socket = new FakeSocket()
    socket.disconnectError = new Error("disconnect rejected xapp-secret-value")
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
    })

    await adapter.start(new AbortController().signal)
    await adapter.stop()

    expect(logger.entries).toContainEqual({
      level: "error",
      message: "socket disconnect failed",
      fields: {
        target: "connection:p1:c1",
        reason: "disconnect rejected <redacted>",
      },
    })
    expect(JSON.stringify(logger.entries)).not.toContain("xapp-secret-value")
  })

  it("does not log an in-flight lease cancellation during shutdown", async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }]
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    let markLeaseStarted!: () => void
    let markLeaseSettled!: () => void
    const leaseStarted = new Promise<void>((resolve) => { markLeaseStarted = resolve })
    const leaseSettled = new Promise<void>((resolve) => { markLeaseSettled = resolve })
    vi.spyOn(transport, "renewLease").mockImplementation(async (_ref, _leaseId, _adapterId, signal) => {
      markLeaseStarted()
      try {
        await new Promise<void>((_resolve, reject) => signal.addEventListener(
          "abort",
          () => reject(new DOMException("This operation was aborted", "AbortError")),
          { once: true },
        ))
      } finally {
        markLeaseSettled()
      }
      throw new Error("unreachable")
    })

    vi.advanceTimersByTime(1_000)
    await leaseStarted
    controller.abort()
    await leaseSettled
    await Promise.resolve()
    await adapter.stop()
    expect(logger.entries).not.toContainEqual(expect.objectContaining({
      level: "error",
      message: "target lease refresh failed",
    }))
  })

  it("uses a validation lease for exactly one hello without creating a runtime", async () => {
    const transport = new FakeTransport()
    const ref = { projectId: "p", connectionId: "c" }
    transport.connections = [ref]
    transport.nextLeases = [{ kind: "validation", leaseId: "validation-lease", generation: 1, expiresAt: "2026-01-01T00:02:00Z", expectedAppId: "A1", appToken: "xapp-candidate-secret" }, null]
    const socket = new FakeSocket()
    let webFactoryCalls = 0
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => {
        webFactoryCalls += 1
        return new FakeWeb()
      },
      discoveryIntervalMs: 60_000,
    })

    await adapter.start(new AbortController().signal)

    expect(socket.starts).toBe(1)
    expect(socket.disconnected).toBe(true)
    expect(transport.hellos).toEqual([{ ref, leaseId: "validation-lease", appId: "A1" }])
    expect(await socket.emit({ team_id: "T", api_app_id: "A1", event: { type: "message", channel: "D", ts: "1", user: "U", text: "ignored" } })).toBe(false)
    expect(transport.envelopes).toEqual([])
    expect(transport.acks).toEqual([])
    expect(webFactoryCalls).toBe(0)
    expect(JSON.stringify(logger.entries)).not.toContain("xapp-candidate-secret")
    await adapter.stop()
  })

  it("does not start a Socket or delivery worker when discovery has no lease", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.nextLeases = [null, null]
    let socketFactoryCalls = 0
    let webFactoryCalls = 0
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => {
        socketFactoryCalls += 1
        return new FakeSocket()
      },
      webFactory: () => {
        webFactoryCalls += 1
        return new FakeWeb()
      },
      discoveryIntervalMs: 60_000,
    })

    await adapter.start(new AbortController().signal)

    expect(socketFactoryCalls).toBe(0)
    expect(webFactoryCalls).toBe(0)
    expect(transport.deliveries).toHaveLength(1)
    await adapter.stop()
  })

  it("disconnects an expired runtime lease before any late Socket event reaches Server", async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    const socket = new FakeSocket()
    vi.spyOn(transport, "renewLease").mockResolvedValue(null)
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await vi.advanceTimersByTimeAsync(1_000)
    expect(socket.disconnected).toBe(true)
    expect(await socket.emit({ team_id: "T", api_app_id: "A1", event: { type: "message", channel: "D", ts: "1", user: "U", text: "late" } })).toBe(false)
    expect(transport.envelopes).toEqual([])
    controller.abort()
    await adapter.stop()
  })

  it("keeps the runtime Socket when a renewal extends the lease", async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextRenewals = [{ leaseId: "lease-c", kind: "runtime", generation: 2, expiresAt: "2026-01-01T00:10:00Z" }]
    const sockets: FakeSocket[] = []
    const socketTokens: string[] = []
    const webTokens: string[] = []
    const webs: FakeWeb[] = []
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: (token) => {
        socketTokens.push(token)
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: (token) => {
        webTokens.push(token)
        const web = new FakeWeb()
        webs.push(web)
        return web
      },
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    transport.deliveries.push({ id: "extended-delivery", conversationId: "D", threadTs: null, payloadJson: JSON.stringify({ text: "extended" }) })
    await vi.advanceTimersByTimeAsync(1_000)

    expect(socketTokens).toEqual(["xapp-c"])
    expect(webTokens).toEqual(["xoxb-c"])
    expect(sockets).toHaveLength(1)
    expect(sockets[0]?.disconnected).toBe(false)
    expect(webs[0]?.posted).toEqual([{ channel: "D", text: "extended" }])
    expect(transport.acks).toEqual([{ ref: { projectId: "p", connectionId: "c" }, id: "extended-delivery", outcome: "delivered" }])
    expect(logger.entries.some((entry) => entry.message === "target lease refresh failed")).toBe(false)
    expect(JSON.stringify(logger.entries)).not.toContain("xapp-")
    expect(JSON.stringify(logger.entries)).not.toContain("xoxb-")
    controller.abort()
    await adapter.stop()
  })

  it("fences the old runtime while a foreign renewal waits for its Socket to disconnect", async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextRenewals = [{ leaseId: "lease-foreign", kind: "runtime", generation: 1, expiresAt: "2026-01-01T00:05:00Z" }]
    const sockets: FakeSocket[] = []
    const webs: FakeWeb[] = []
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => {
        const web = new FakeWeb()
        webs.push(web)
        return web
      },
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 100,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    let releaseDisconnect!: () => void
    let markDisconnectStarted!: () => void
    const disconnectGate = new Promise<void>((resolve) => { releaseDisconnect = resolve })
    const disconnectStarted = new Promise<void>((resolve) => { markDisconnectStarted = resolve })
    sockets[0]!.disconnectGate = disconnectGate
    sockets[0]!.disconnectStarted = markDisconnectStarted

    await vi.advanceTimersByTimeAsync(1_000)
    await disconnectStarted
    const claimsWhileDisconnected = transport.claimDeliveryCalls
    transport.deliveries.push({ id: "foreign-delivery", conversationId: "D", threadTs: null, payloadJson: JSON.stringify({ text: "pending" }) })
    await vi.advanceTimersByTimeAsync(100)

    expect(transport.claimDeliveryCalls).toBe(claimsWhileDisconnected)
    expect(webs[0]?.posted).toEqual([])
    expect(transport.acks).toEqual([])

    transport.deliveries.length = 0
    releaseDisconnect()
    await vi.advanceTimersByTimeAsync(0)
    expect(sockets[0]?.disconnected).toBe(true)
    expect(await sockets[0]!.emit({ team_id: "T", api_app_id: "A1", event: { type: "message", channel: "D", ts: "1", user: "U", text: "late" } })).toBe(false)
    expect(transport.envelopes).toEqual([])
    controller.abort()
    await adapter.stop()
  })

  it("re-acquires a rotated runtime even when the old Socket disconnect fails", async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextRenewals = [{ leaseId: "lease-foreign", kind: "runtime", generation: 1, expiresAt: "2026-01-01T00:05:00Z" }]
    const sockets: FakeSocket[] = []
    const webs: FakeWeb[] = []
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => {
        const web = new FakeWeb()
        webs.push(web)
        return web
      },
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 100,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    let releaseDisconnect!: () => void
    let markDisconnectStarted!: () => void
    const disconnectGate = new Promise<void>((resolve) => { releaseDisconnect = resolve })
    const disconnectStarted = new Promise<void>((resolve) => { markDisconnectStarted = resolve })
    sockets[0]!.disconnectGate = disconnectGate
    sockets[0]!.disconnectStarted = markDisconnectStarted
    sockets[0]!.disconnectError = new Error("disconnect rejected xapp-secret-value")

    await vi.advanceTimersByTimeAsync(1_000)
    await disconnectStarted
    await vi.advanceTimersByTimeAsync(1_000)

    expect(sockets).toHaveLength(1)
    releaseDisconnect()
    await vi.advanceTimersByTimeAsync(0)

    transport.nextLeases = [null, { kind: "runtime", leaseId: "lease-rotated", generation: 1, expiresAt: "2026-01-01T00:05:00Z", appToken: "xapp-rotated", botToken: "xoxb-rotated" }]
    await adapter.refreshConnections(controller.signal)
    expect(sockets).toHaveLength(2)
    expect(sockets[1]?.starts).toBe(1)

    transport.deliveries.push({ id: "current-delivery", conversationId: "D", threadTs: null, payloadJson: JSON.stringify({ text: "current" }) })
    await vi.advanceTimersByTimeAsync(100)

    expect(sockets[1]?.disconnected).toBe(false)
    expect(webs[1]?.posted).toEqual([{ channel: "D", text: "current" }])
    expect(transport.acks).toEqual([{ ref: { projectId: "p", connectionId: "c" }, id: "current-delivery", outcome: "delivered" }])
    expect(logger.entries).toContainEqual({
      level: "error",
      message: "socket disconnect failed",
      fields: { target: "connection:p:c", reason: "disconnect rejected <redacted>" },
    })
    expect(logger.entries.some((entry) => entry.message === "target lease refresh failed")).toBe(false)
    expect(JSON.stringify(logger.entries)).not.toContain("xapp-secret-value")
    controller.abort()
    await adapter.stop()
  })

  it("reopens a runtime Socket with rotated credentials after a superseded lease is re-acquired", async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextRenewals = [{ leaseId: "lease-foreign", kind: "runtime", generation: 1, expiresAt: "2026-01-01T00:05:00Z" }]
    const sockets: FakeSocket[] = []
    const socketTokens: string[] = []
    const webTokens: string[] = []
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: (token) => {
        socketTokens.push(token)
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: (token) => {
        webTokens.push(token)
        return new FakeWeb()
      },
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await vi.advanceTimersByTimeAsync(1_000)
    expect(sockets[0]?.disconnected).toBe(true)

    transport.nextLeases = [null, { kind: "runtime", leaseId: "lease-rotated", generation: 1, expiresAt: "2026-01-01T00:05:00Z", appToken: "xapp-rotated", botToken: "xoxb-rotated" }]
    await adapter.refreshConnections(controller.signal)

    expect(socketTokens).toEqual(["xapp-c", "xapp-rotated"])
    expect(webTokens).toEqual(["xoxb-c", "xoxb-rotated"])
    expect(sockets[0]?.disconnected).toBe(true)
    expect(sockets[1]?.starts).toBe(1)
    expect(JSON.stringify(logger.entries)).not.toContain("xapp-rotated")
    expect(JSON.stringify(logger.entries)).not.toContain("xoxb-rotated")
    controller.abort()
    await adapter.stop()
  })

  it("fences a stale renewal that resolves after the runtime was removed", async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    const sockets: FakeSocket[] = []
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const claimsBefore = transport.claimDeliveryCalls
    let releaseRenew!: () => void
    let markRenewStarted!: () => void
    const renewGate = new Promise<void>((resolve) => { releaseRenew = resolve })
    const renewStarted = new Promise<void>((resolve) => { markRenewStarted = resolve })
    vi.spyOn(transport, "renewLease").mockImplementation(async (ref, leaseId) => {
      markRenewStarted()
      await renewGate
      return { leaseId, kind: "runtime", generation: 1, expiresAt: "2026-01-01T00:05:00Z" }
    })

    await vi.advanceTimersByTimeAsync(1_000)
    await renewStarted
    transport.connections = []
    await adapter.refreshConnections(controller.signal)
    expect(sockets[0]?.disconnected).toBe(true)

    releaseRenew()
    await vi.advanceTimersByTimeAsync(0)
    expect(sockets).toHaveLength(1)
    expect(transport.claimDeliveryCalls).toBe(claimsBefore)
    expect(logger.entries.some((entry) => entry.message === "target lease refresh failed")).toBe(false)
    controller.abort()
    await adapter.stop()
  })

  it("does not forward a Socket event that was waiting when a foreign renewal removes the runtime", async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextRenewals = [{ leaseId: "lease-foreign", kind: "runtime", generation: 1, expiresAt: "2026-01-01T00:05:00Z" }]
    let releaseIngress!: () => void
    let markIngressStarted!: () => void
    transport.ingressGate = new Promise<void>((resolve) => { releaseIngress = resolve })
    const ingressStarted = new Promise<void>((resolve) => { markIngressStarted = resolve })
    transport.ingressStarted = markIngressStarted
    const sockets: FakeSocket[] = []
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
      maxInFlight: 1,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    const first = sockets[0]!.emit({ team_id: "T", api_app_id: "A1", event: { type: "message", channel: "D", ts: "1", user: "U", text: "first" } })
    await ingressStarted
    const old = sockets[0]!.emit({ team_id: "T", api_app_id: "A1", event: { type: "message", channel: "D", ts: "2", user: "U", text: "old" } })
    await vi.advanceTimersByTimeAsync(1_000)
    releaseIngress()
    await first
    await vi.advanceTimersByTimeAsync(5)
    await old

    expect(transport.envelopes.map((envelope) => envelope.messageTs)).toEqual(["1"])
    expect(sockets).toHaveLength(1)
    controller.abort()
    await adapter.stop()
  })

  it("a stale error from a superseded runtime never evicts its replacement", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    let releaseIngress!: () => void
    let markIngressStarted!: () => void
    transport.ingressGate = new Promise<void>((resolve) => { releaseIngress = resolve })
    const ingressStarted = new Promise<void>((resolve) => { markIngressStarted = resolve })
    transport.ingressStarted = markIngressStarted
    const sockets: FakeSocket[] = []
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => new FakeWeb(),
      discoveryIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    expect(sockets).toHaveLength(1)

    // Runtime A stalls inside ingress with an open event.
    const pending = sockets[0]!.emit({ team_id: "T", api_app_id: "A1", event: { type: "message", channel: "D", ts: "1", user: "U", text: "first" } })
    await ingressStarted

    // Discovery evicts A, then re-acquires the same target as runtime B.
    transport.connections = []
    await adapter.refreshConnections(controller.signal)
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    await adapter.refreshConnections(controller.signal)
    expect(sockets).toHaveLength(2)
    expect(sockets[1]!.started).toBe(true)

    // A's stalled ingress fails stale: its removal must evict A but never B.
    transport.ingressError = new LeaseStaleError()
    releaseIngress()
    await pending
    transport.ingressError = undefined

    expect(sockets[0]!.disconnected).toBe(true)
    expect(sockets[1]!.disconnected).toBe(false)
    // B still owns the runtime: its socket events keep being forwarded.
    await sockets[1]!.emit({ team_id: "T", api_app_id: "A1", event: { type: "message", channel: "D", ts: "2", user: "U", text: "second" } })
    expect(transport.envelopes.map((envelope) => envelope.messageTs)).toEqual(["1", "2"])
    controller.abort()
    await adapter.stop()
  })

  it("stops an old drain before claim, mutation, or acknowledgement when renewal expires", async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    const web = new FakeWeb()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => web,
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 100,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const claimsBefore = transport.claimDeliveryCalls
    transport.deliveries.push({ id: "old-delivery", conversationId: "D", threadTs: null, payloadJson: JSON.stringify({ text: "old" }) })
    let releaseUncertain!: () => void
    let markUncertainStarted!: () => void
    transport.uncertainGate = new Promise<void>((resolve) => { releaseUncertain = resolve })
    const uncertainStarted = new Promise<void>((resolve) => { markUncertainStarted = resolve })
    transport.uncertainStarted = markUncertainStarted

    vi.advanceTimersByTime(100)
    await uncertainStarted
    transport.nextRenewals = [null]
    await vi.advanceTimersByTimeAsync(900)
    releaseUncertain()
    await vi.advanceTimersByTimeAsync(0)

    expect(transport.claimDeliveryCalls).toBe(claimsBefore)
    expect(web.posted).toEqual([])
    expect(transport.acks).toEqual([])
    controller.abort()
    await adapter.stop()
  })

  it("normalizes the Socket Mode interactive payload without copying its raw body", () => {
    const interaction = normalizeSlackInteraction({
      type: "interactive",
      payload: JSON.stringify({
        type: "block_actions",
        api_app_id: "A1",
        trigger_id: "trigger-1",
        team: { id: "T1" },
        user: { id: "U1" },
        container: { channel_id: "C1", message_ts: "123.456", thread_ts: "123.000" },
        actions: [{ action_id: "mohist_stop_turn", action_ts: "123.500", value: "server-signed-value" }],
        token: "xoxb-secret",
      }),
    })

    expect(interaction).toEqual({
      eventType: "block_actions",
      apiAppId: "A1",
      interactionId: "trigger-1",
      teamId: "T1",
      conversationId: "C1",
      messageTs: "123.456",
      threadTs: "123.000",
      actorSlackUserId: "U1",
      actionId: "mohist_stop_turn",
      actionValue: "server-signed-value",
    })
    expect(JSON.stringify(interaction)).not.toContain("xoxb-secret")
  })

  it("acknowledges an interaction before waiting for Server processing", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    let releaseInteraction!: () => void
    let markInteractionStarted!: () => void
    transport.interactionGate = new Promise<void>((resolve) => { releaseInteraction = resolve })
    const interactionStarted = new Promise<void>((resolve) => { markInteractionStarted = resolve })
    transport.interactionStarted = markInteractionStarted
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const pending = socket.emit({
      type: "block_actions",
        api_app_id: "A1",
      trigger_id: "trigger-1",
      team: { id: "T1" },
      user: { id: "U1" },
      container: { channel_id: "C1", message_ts: "123.456" },
      actions: [{ action_id: "mohist_stop_turn", action_ts: "123.500", value: "signed-value" }],
    })

    await interactionStarted
    expect(transport.interactions).toHaveLength(1)
    expect(socket.acknowledged).toBe(true)
    releaseInteraction()
    await expect(pending).resolves.toBe(true)
    controller.abort()
  })

  it("contains a failed acknowledged interaction without crashing the socket callback", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.interactionError = new Error("Server returned 500 with xoxb-secret")
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const body = {
      type: "block_actions",
        api_app_id: "A1",
      trigger_id: "trigger-1",
      team: { id: "T1" },
      user: { id: "U1" },
      container: { channel_id: "C1", message_ts: "123.456" },
      actions: [{ action_id: "mohist_stop_turn", action_ts: "123.500", value: "signed-value" }],
    }

    await expect(socket.emit(body)).resolves.toBe(true)
    expect(logger.entries).toContainEqual({
      level: "error",
      message: "interaction processing failed after acknowledgement",
      fields: {
        target: "connection:p:c",
        event: "block_actions",
        reason: "Server returned 500 with <redacted>",
      },
    })

    transport.interactionError = undefined
    await expect(socket.emit(body)).resolves.toBe(true)
    controller.abort()
  })

  it("leaves a failed message event unacknowledged for Slack retry", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.ingressError = new Error("Server returned 500")
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await expect(socket.emit({
      team_id: "T1", api_app_id: "A1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "123.456", user: "U1", text: "do work" },
    })).resolves.toBe(false)
    expect(logger.entries).toContainEqual({
      level: "error",
      message: "event handling failed before acknowledgement",
      fields: {
        target: "connection:p:c",
        event: "message",
        reason: "Server returned 500",
      },
    })
    controller.abort()
  })

  it("normalizes a Socket Mode event with stable identity", () => {
    expect(normalizeSocketEvent({
      team_id: "T1", api_app_id: "A1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "123.456", user: "U1", text: "do work" },
    })).toEqual({
      eventType: "message",
      apiAppId: "A1",
      isDirectMessage: true,
      teamId: "T1",
      conversationId: "D1",
      messageTs: "123.456",
      threadTs: null,
      mentionedUserIds: [],
      senderSlackUserId: "U1",
      senderKind: "human",
      text: "do work",
      files: [],
    })
  })

  it("forwards file metadata without Slack secrets or raw payload", () => {
    const envelope = normalizeSocketEvent({
      team_id: "T1", api_app_id: "A1",
      bot_token: "xoxb-secret",
      event: {
        type: "message",
        subtype: "file_share",
        channel: "D1",
        ts: "123.456",
        user: "U1",
        text: "read these",
        files: [
          {
            id: "F1",
            name: "report.txt",
            mimetype: "text/plain",
            size: 42,
            url_private: "https://files.slack.com/secret",
            url_private_download: "https://files.slack.com/download-secret",
          },
          {
            id: "F2",
            name: "image.png",
            mimetype: "image/png",
            size: 2048,
            permalink: "https://workspace.slack.com/files/F2",
          },
        ],
      },
    })

    expect(envelope.files).toEqual([
      { id: "F1", name: "report.txt", mimetype: "text/plain", size: 42 },
      { id: "F2", name: "image.png", mimetype: "image/png", size: 2048 },
    ])
    expect(JSON.stringify(envelope)).not.toContain("url_private")
    expect(JSON.stringify(envelope)).not.toContain("xoxb-secret")
    expect(envelope).not.toHaveProperty("event")
  })

  it("normalizes channel threads, all mentions, bot senders, and unknown senders", () => {
    expect(normalizeSocketEvent({
      team_id: "T1", api_app_id: "A1",
      event: {
        type: "message",
        channel: "C1",
        ts: "123.456",
        thread_ts: "123.000",
        user: "U1",
        text: "<@B1> ask <@B2|other> and <@B1> again",
      },
    })).toMatchObject({
      threadTs: "123.000",
      mentionedUserIds: ["B1", "B2"],
      senderSlackUserId: "U1",
      senderKind: "human",
    })

    expect(normalizeSocketEvent({
      team_id: "T1", api_app_id: "A1",
      event: { channel: "C1", ts: "123.457", subtype: "bot_message", bot_id: "B1", text: "reply" },
    })).toMatchObject({
      teamId: "T1",
      conversationId: "C1",
      messageTs: "123.457",
      senderSlackUserId: null,
      senderKind: "bot",
    })

    expect(normalizeSocketEvent({
      team_id: "T1", api_app_id: "A1",
      event: { channel: "C1", ts: "123.458", text: "system event" },
    })).toMatchObject({
      teamId: "T1",
      conversationId: "C1",
      messageTs: "123.458",
      senderSlackUserId: null,
      senderKind: "unknown",
    })
  })

  it("acknowledges bot and unknown events without requiring a user id", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await expect(socket.emit({
      team_id: "T1", api_app_id: "A1",
      event: { channel: "C1", ts: "123.457", subtype: "bot_message", bot_id: "B1", text: "reply" },
    })).resolves.toBe(true)
    await expect(socket.emit({
      team_id: "T1", api_app_id: "A1",
      event: { channel: "C1", ts: "123.458", text: "system event" },
    })).resolves.toBe(true)
    expect(transport.envelopes.map((envelope) => [envelope.messageTs, envelope.senderKind])).toEqual([
      ["123.457", "bot"],
      ["123.458", "unknown"],
    ])
    controller.abort()
  })

  it("discovers connections, forwards every event to ingress, and drains replies", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }, { projectId: "p1", connectionId: "c2" }]
    const sockets = new Map<string, FakeSocket>()
    const webs = new Map<string, FakeWeb>()
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      transport,
      socketFactory: (_token, ref) => {
        const socket = new FakeSocket()
        sockets.set(ref.connectionId, socket)
        return socket
      },
      webFactory: (_token, ref) => {
        const web = new FakeWeb()
        webs.set(ref.connectionId, web)
        return web
      },
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })

    const controller = new AbortController()
    await adapter.start(controller.signal)
    expect(transport.leases.map((ref) => ref.connectionId)).toEqual(["c1", "c2"])
    expect(sockets.get("c1")?.started).toBe(true)
    expect(await sockets.get("c1")?.emit({ team_id: "T1", api_app_id: "A1", event: { type: "message", channel: "D1", channel_type: "im", ts: "1.2", user: "U1", text: "task" } })).toBe(true)
    expect(transport.envelopes).toHaveLength(1)
    expect(transport.envelopes[0]?.text).toBe("task")
    expect(logger.entries).toContainEqual({
      level: "info",
      message: "envelope received",
      fields: { target: "connection:p1:c1", event: "message" },
    })
    expect(logger.entries).toContainEqual({
      level: "info",
      message: "envelope forwarding",
      fields: { target: "connection:p1:c1", event: "message" },
    })
    expect(logger.entries).toContainEqual({
      level: "info",
      message: "ingress accepted",
      fields: { target: "connection:p1:c1", event: "message", kind: "accepted" },
    })
    expect(webs.get("c1")?.posted).toEqual([{ channel: "D1", text: "accepted" }])
    expect(transport.acks).toEqual([{ ref: { projectId: "p1", connectionId: "c1" }, id: "delivery-1", outcome: "delivered" }])
    controller.abort()
  })

  it("runs an explicit Manager target through its Socket Mode runtime lease", async () => {
    const manager: SlackManagerRef = {
      kind: "manager",
      enrollmentId: "enrollment-manager",
      workspaceTeamId: "T_MANAGER",
    }
    const transport = new FakeTransport()
    transport.connections = [manager]
    transport.deliveries = [{
      id: "manager-delivery",
      ownerKind: "manager",
      conversationId: "D_MANAGER",
      threadTs: null,
      payloadJson: JSON.stringify({ text: "manager reply" }),
    }]
    transport.uncertainDeliveries.push({
      id: "manager-uncertain",
      ownerKind: "manager",
      conversationId: "D_MANAGER",
      threadTs: null,
      payloadJson: JSON.stringify({ text: "manager uncertain reply" }),
    })
    const web = new FakeWeb()
    let socketFactoryCalls = 0
    let webFactoryToken: string | undefined
    const adapter = new SlackAdapter({
      adapterId: "adapter-manager",
      transport,
      socketFactory: () => {
        socketFactoryCalls += 1
        return new FakeSocket()
      },
      webFactory: (botToken) => {
        webFactoryToken = botToken
        return web
      },
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)

    expect(socketFactoryCalls).toBe(1)
    expect(webFactoryToken).toBe("xoxb-enrollment-manager")
    expect(transport.leases).toEqual([manager])
    expect(web.posted).toEqual([{ channel: "D_MANAGER", text: "manager reply" }])
    expect(transport.acks).toEqual([
      { ref: manager, id: "manager-uncertain", outcome: "uncertain" },
      { ref: manager, id: "manager-delivery", outcome: "delivered" },
    ])
    controller.abort()
  })

  it("posts a Server-generated Open in Mohist block without interpreting reply text", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }]
    const blocks = [{
      type: "actions",
      elements: [{ type: "button", text: { type: "plain_text", text: "Open in Mohist" }, url: "https://mohist.example/demo/sessions/session-1" }],
    }]
    transport.deliveries = [{
      id: "terminal-1",
      conversationId: "D1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "post_message",
        text: 'Completed. Agent said {"blocks":[]}.',
        clientMessageId: "terminal:1",
        blocks,
      }),
    }]
    const socket = new FakeSocket()
    const web = new FakeWeb()
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)

    expect(web.posted).toEqual([{
      channel: "D1",
      text: 'Completed. Agent said {"blocks":[]}.',
      thread_ts: "100.001",
      client_msg_id: "terminal:1",
      blocks,
    }])
    controller.abort()
  })

  it("uploads a local image file as a Slack file share reply", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }]
    transport.deliveries = [{
      id: "delivery-file",
      conversationId: "D1",
      threadTs: "1710000000.000100",
      payloadJson: JSON.stringify({
        operation: "upload_file",
        text: "screenshot attached",
        fileName: "shot.png",
        fileContentBase64: Buffer.from("png-bytes").toString("base64"),
      }),
    }, {
      id: "delivery-file-dm",
      conversationId: "D2",
      threadTs: null,
      payloadJson: JSON.stringify({
        operation: "upload_file",
        fileName: "shot.png",
        fileContentBase64: Buffer.from("png-bytes").toString("base64"),
      }),
    }]
    const web = new FakeWeb()
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)

    expect(web.uploaded).toHaveLength(2)
    expect(web.uploaded[0]).toMatchObject({
      channels: "D1",
      thread_ts: "1710000000.000100",
      filename: "shot.png",
      initial_comment: "screenshot attached",
    })
    expect(web.uploaded[0]?.file).toEqual(Buffer.from("png-bytes"))
    expect(web.uploaded[1]).toMatchObject({ channel_id: "D2", filename: "shot.png" })
    expect(web.uploaded[1]?.initial_comment).toBeUndefined()
    expect(transport.acks).toEqual([
      { ref: { projectId: "p1", connectionId: "c1" }, id: "delivery-file", outcome: "delivered" },
      { ref: { projectId: "p1", connectionId: "c1" }, id: "delivery-file-dm", outcome: "delivered" },
    ])
    controller.abort()
  })

  it("posts an image-only reply with blocks and without a text body", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }]
    const blocks = [{ type: "image", image_url: "https://example.com/p.png", alt_text: "Image" }]
    transport.deliveries = [{
      id: "delivery-image",
      conversationId: "D1",
      threadTs: null,
      payloadJson: JSON.stringify({
        operation: "post_message",
        clientMessageId: "slack-reply:c1:D1:dm:image",
        blocks,
      }),
    }]
    const web = new FakeWeb()
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)

    expect(web.posted).toEqual([{
      channel: "D1",
      text: "",
      client_msg_id: "slack-reply:c1:D1:dm:image",
      blocks,
    }])
    expect(transport.acks).toEqual([
      { ref: { projectId: "p1", connectionId: "c1" }, id: "delivery-image", outcome: "delivered" },
    ])
    controller.abort()
  })

  it("records the file share identity from the upload response for reconciliation", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }]
    transport.deliveries = [{
      id: "delivery-file-identity",
      conversationId: "C_PUBLIC",
      threadTs: null,
      payloadJson: JSON.stringify({
        operation: "upload_file",
        fileName: "shot.png",
        fileContentBase64: Buffer.from("png-bytes").toString("base64"),
      }),
    }]
    const web = new FakeWeb()
    web.nextUploadResponses = [{
      ok: true,
      files: [{ ok: true, files: [{ id: "F1", shares: { public: { C_PUBLIC: [{ ts: "1710000000.000200" }] } } }] }],
    }]
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)

    expect(transport.acks).toEqual([
      { ref: { projectId: "p1", connectionId: "c1" }, id: "delivery-file-identity", outcome: "delivered", providerMessageIdentity: { conversationId: "C_PUBLIC", messageTs: "1710000000.000200" } },
    ])
    controller.abort()
  })

  it("forwards interactions to the Server and drains its block update after acknowledging", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }]
    transport.deliveries = [{
      id: "control-update",
      conversationId: "C1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "chat_update",
        text: "Stop requested. Waiting for the runtime to confirm.",
        providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
        blocks: [{ type: "section", text: { type: "mrkdwn", text: "Stop requested. Waiting for the runtime to confirm." } }],
      }),
    }]
    const socket = new FakeSocket()
    const web = new FakeWeb()
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    const acknowledged = await socket.emit({
      type: "interactive",
      payload: JSON.stringify({
        type: "block_actions",
        api_app_id: "A1",
        trigger_id: "trigger-1",
        team: { id: "T1" },
        user: { id: "U1" },
        container: { channel_id: "C1", message_ts: "100.002", thread_ts: "100.001" },
        actions: [{ action_id: "mohist_stop_turn", value: "server-signed-value" }],
      }),
    })

    expect(acknowledged).toBe(true)
    expect(transport.interactions).toEqual([{
      eventType: "block_actions",
      apiAppId: "A1",
      interactionId: "trigger-1",
      teamId: "T1",
      conversationId: "C1",
      messageTs: "100.002",
      threadTs: "100.001",
      actorSlackUserId: "U1",
      actionId: "mohist_stop_turn",
      actionValue: "server-signed-value",
    }])
    expect(web.updated).toEqual([{
      channel: "C1",
      ts: "100.002",
      text: "Stop requested. Waiting for the runtime to confirm.",
      blocks: [{ type: "section", text: { type: "mrkdwn", text: "Stop requested. Waiting for the runtime to confirm." } }],
    }])
    expect(transport.acks).toEqual([{ ref: { projectId: "p1", connectionId: "c1" }, id: "control-update", outcome: "delivered", providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" } }])
    controller.abort()
  })

  it("starts with zero connections and reconciles later additions and removals", async () => {
    const transport = new FakeTransport()
    const sockets = new Map<string, FakeSocket>()
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      transport,
      socketFactory: (_token, ref) => {
        const socket = new FakeSocket()
        sockets.set(ref.connectionId, socket)
        return socket
      },
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await expect(adapter.start(controller.signal)).resolves.toBeUndefined()
    expect(transport.leases).toEqual([])

    transport.connections = [{ projectId: "p", connectionId: "c" }]
    await adapter.refreshConnections(controller.signal)
    expect(sockets.get("c")?.started).toBe(true)

    transport.connections = []
    await adapter.refreshConnections(controller.signal)
    expect(sockets.get("c")?.disconnected).toBe(true)
    controller.abort()
  })

  it("posts threaded deliveries in a thread and DM deliveries without a thread target", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.push({ id: "delivery-2", conversationId: "C1", threadTs: "1.2", payloadJson: JSON.stringify({ text: "thread reply" }) })
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    expect(web.posted).toEqual([
      { channel: "D1", text: "accepted" },
      { channel: "C1", text: "thread reply", thread_ts: "1.2" },
    ])
    controller.abort()
  })

  it("posts the backpressured reason to the originating conversation so the sender can see the refusal", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [
      { kind: "backpressured", reason: "This Slack Connection is backpressured; retry after pending deliveries drain." },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    const acknowledged = await socket.emit({
      team_id: "T1", api_app_id: "A1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "123.456", thread_ts: "123.000", user: "U1", text: "do work" },
    })

    expect(acknowledged).toBe(true)
    expect(web.posted).toEqual([
      { channel: "D1", text: "This Slack Connection is backpressured; retry after pending deliveries drain.", thread_ts: "123.000" },
    ])
    expect(transport.acks).toEqual([])
    controller.abort()
  })

  it("does not render server-enqueued rejected kinds so the outbox reply is not duplicated", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [
      { kind: "rejected", reason: "Please send a task for the Agent to perform." },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    const acknowledged = await socket.emit({
      team_id: "T1", api_app_id: "A1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "123.456", user: "U1", text: "" },
    })

    expect(acknowledged).toBe(true)
    expect(web.posted).toEqual([])
    controller.abort()
  })

  it("distinguishes a backpressured refusal from an accepted result that is still pending", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [
      { kind: "accepted" },
      { kind: "backpressured", reason: "retry shortly" },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    const firstAck = await socket.emit({
      team_id: "T1", api_app_id: "A1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "1710000000.000001", user: "U1", text: "first" },
    })
    const secondAck = await socket.emit({
      team_id: "T1", api_app_id: "A1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "1710000000.000002", user: "U1", text: "second" },
    })

    expect(firstAck).toBe(true)
    expect(secondAck).toBe(true)
    expect(web.posted).toEqual([
      { channel: "D1", text: "retry shortly" },
    ])
    controller.abort()
  })

  it("acks explicit Slack rejections as retry so the same post can be re-sent without duplicating", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries = [{ id: "delivery-rejected", conversationId: "D1", threadTs: null, payloadJson: JSON.stringify({ text: "ok?" }) }]
    transport.deliveries.push({ id: "delivery-accepted", conversationId: "D1", threadTs: null, payloadJson: JSON.stringify({ text: "ok?" }) })
    const web = new FakeWeb()
    web.nextResponses = [
      { ok: false, error: "channel_not_found" },
      { ok: true },
    ]
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    expect(web.posted).toHaveLength(2)
    expect(transport.acks).toEqual([
      { ref: { projectId: "p", connectionId: "c" }, id: "delivery-rejected", outcome: "retry" },
      { ref: { projectId: "p", connectionId: "c" }, id: "delivery-accepted", outcome: "delivered" },
    ])
    expect(web.posted).toEqual([
      { channel: "D1", text: "ok?" },
      { channel: "D1", text: "ok?" },
    ])
    controller.abort()
  })

  it("acks transport or payload errors as uncertain so the row is held for operator action", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries = [
      { id: "delivery-bad-json", conversationId: "D1", threadTs: null, payloadJson: "not-json" },
      { id: "delivery-no-text", conversationId: "D1", threadTs: null, payloadJson: JSON.stringify({ text: null }) },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    expect(transport.acks).toHaveLength(2)
    expect(transport.acks).toEqual([
      { ref: { projectId: "p", connectionId: "c" }, id: "delivery-bad-json", outcome: "uncertain" },
      { ref: { projectId: "p", connectionId: "c" }, id: "delivery-no-text", outcome: "uncertain" },
    ])
    expect(web.posted).toEqual([])
    controller.abort()
  })

  it("limits concurrent ingress without using a durable queue", async () => {
    const socket = new FakeSocket()
    let active = 0
    let maximum = 0
    let releaseFirst!: () => void
    let markFirstStarted!: () => void
    const first = new Promise<void>((resolve) => { releaseFirst = resolve })
    const firstStarted = new Promise<void>((resolve) => { markFirstStarted = resolve })
    const transport: AdapterTransport = {
      discover: async () => [{ projectId: "p", connectionId: "c" }],
      acquireLease: async (_ref, kind) => kind === "validation"
        ? null
        : { kind: "runtime", leaseId: "lease", generation: 1, expiresAt: "2026-01-01T00:05:00Z", appToken: "app", botToken: "bot" },
      renewLease: async () => ({ leaseId: "lease", kind: "runtime", generation: 1, expiresAt: "2026-01-01T00:05:00Z" }),
      reportHello: async () => "verified",
      ingress: async () => {
        active += 1
        maximum = Math.max(maximum, active)
        if (active === 1) {
          markFirstStarted()
          await first
        }
        active -= 1
        return { kind: "accepted" }
      },
      interaction: async () => ({ state: "stop_requested" }),
      claimDelivery: async () => null,
      ackDelivery: async () => undefined,
    }
    vi.useFakeTimers()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      maxInFlight: 1,
      deliveryPollIntervalMs: 60_000,
      heartbeatIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const event = { team_id: "T", api_app_id: "A1", event: { channel: "D", ts: "1", user: "U", text: "x" } }
    const firstEvent = socket.emit(event)
    await firstStarted
    const secondEvent = socket.emit({ ...event, event: { ...event.event, ts: "2" } })
    await vi.advanceTimersByTimeAsync(10)
    expect(maximum).toBe(1)
    releaseFirst()
    await vi.advanceTimersByTimeAsync(5)
    await Promise.all([firstEvent, secondEvent])
    controller.abort()
  })
})
