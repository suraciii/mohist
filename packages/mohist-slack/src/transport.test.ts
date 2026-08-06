import { describe, expect, it } from "vitest"
import { HttpAdapterTransport, LeaseStaleError } from "./transport.js"
import type { SlackEnvelope, SlackInteractionEnvelope } from "./types.js"

const envelope: SlackEnvelope = {
  eventType: "message",
  apiAppId: "A1",
  isDirectMessage: true,
  teamId: "T",
  conversationId: "D",
  messageTs: "1",
  threadTs: null,
  mentionedUserIds: [],
  senderSlackUserId: "U",
  senderKind: "human",
  text: "task",
  files: [],
}

const interaction: SlackInteractionEnvelope = {
  eventType: "block_actions",
  apiAppId: "A1",
  interactionId: "interaction-1",
  teamId: "T",
  conversationId: "D",
  messageTs: "2",
  threadTs: null,
  actorSlackUserId: "U",
  actionId: "mohist_stop_turn",
  actionValue: "server-signed-value",
}

describe("HttpAdapterTransport", () => {
  it("uses the canonical operator-authenticated lease endpoints for discovery, hello, renew, ingress, and deliveries", async () => {
    const calls: Array<{ url: string; method: string; body: string; operatorToken: string | null; operatorId: string | null }> = []
    const transport = new HttpAdapterTransport({
      serverUrl: "http://localhost/",
      operatorToken: "operator",
      operatorId: "operator-id",
      fetch: async (input, init) => {
        const url = String(input)
        const requestBody = JSON.parse(String(init?.body ?? "{}")) as { kind?: string }
        calls.push({
          url,
          method: String(init?.method ?? "GET"),
          body: String(init?.body ?? ""),
          operatorToken: new Headers(init?.headers).get("x-mohist-operator-token"),
          operatorId: new Headers(init?.headers).get("x-mohist-operator-id"),
        })
        const data = url.endsWith("/api/slack-adapter/leases/targets")
          ? [
              { kind: "connection", projectId: "p", connectionId: "c", expectedAppId: "A1", active: true, appToken: "xapp-discovery-secret" },
              { kind: "manager", enrollmentId: "e", workspaceTeamId: "T_MANAGER", expectedAppId: "A2", active: true, appToken: "xapp-manager-discovery-secret" },
            ]
          : url.endsWith("/leases/acquire")
            ? requestBody.kind === "validation"
              ? { leaseId: "lease-validation", generation: 1, expiresAt: "2026-01-01T00:02:00Z", expectedAppId: "A1", appToken: "xapp-validation" }
              : { leaseId: "lease", generation: 1, expiresAt: "2026-01-01T00:05:00Z", appToken: "xapp", botToken: "xoxb" }
            : url.endsWith("/leases/hello") ? { outcome: "verified" }
              : url.endsWith("/leases/renew")
                ? { leaseId: "lease", kind: "runtime", generation: 2, expiresAt: "2026-01-01T00:10:00Z" }
                : url.endsWith("/slack-manager/ingress") ? { kind: "accepted" }
                  : url.endsWith("/ingress") ? { kind: "accepted" }
                    : url.endsWith("/interactions") ? { state: "stop_requested" }
                      : url.endsWith("/claim-uncertain")
                        ? { id: "manager-uncertain", ownerKind: "manager", conversationId: "D_MANAGER", threadTs: null, payloadJson: '{"text":"uncertain"}' }
                        : url.endsWith("/claim")
                          ? { id: url.includes("/slack-manager/") ? "manager-delivery" : "delivery-1", ownerKind: url.includes("/slack-manager/") ? "manager" : "connection", conversationId: "D1", threadTs: null, payloadJson: '{"text":"reply"}' }
                          : null
        return new Response(JSON.stringify({ success: true, data }), { status: 200 })
      },
    })
    const ref = { projectId: "p", connectionId: "c" }
    const manager = { kind: "manager" as const, enrollmentId: "e", workspaceTeamId: "T_MANAGER" }
    const signal = new AbortController().signal
    const discovered = await transport.discover(signal)
    expect(discovered).toEqual([ref, manager])
    expect(JSON.stringify(discovered)).not.toContain("xapp-discovery-secret")
    expect(JSON.stringify(discovered)).not.toContain("xapp-manager-discovery-secret")
    await expect(transport.acquireLease(ref, "validation", "a", signal)).resolves.toMatchObject({
      kind: "validation",
      leaseId: "lease-validation",
      generation: 1,
      expectedAppId: "A1",
      appToken: "xapp-validation",
    })
    await expect(transport.acquireLease(ref, "runtime", "a", signal)).resolves.toEqual({
      kind: "runtime",
      leaseId: "lease",
      generation: 1,
      expiresAt: "2026-01-01T00:05:00Z",
      appToken: "xapp",
      botToken: "xoxb",
    })
    await expect(transport.acquireLease(manager, "runtime", "a", signal)).resolves.toMatchObject({ kind: "runtime", leaseId: "lease" })
    await expect(transport.renewLease(ref, "lease", "a", signal)).resolves.toEqual({
      leaseId: "lease",
      kind: "runtime",
      generation: 2,
      expiresAt: "2026-01-01T00:10:00Z",
    })
    await expect(transport.reportHello(ref, "lease", "A1", signal)).resolves.toBe("verified")
    await expect(transport.ingress(ref, envelope, "lease", "a", signal)).resolves.toEqual({ kind: "accepted" })
    await expect(transport.ingress(manager, envelope, "lease", "a", signal)).resolves.toEqual({ kind: "accepted" })
    await expect(transport.interaction(ref, interaction, "lease", "a", signal)).resolves.toEqual({ state: "stop_requested" })
    await expect(transport.claimDelivery(ref, "lease", "a", signal)).resolves.toEqual({
      id: "delivery-1",
      ownerKind: "connection",
      conversationId: "D1",
      threadTs: null,
      payloadJson: '{"text":"reply"}',
    })
    await transport.ackDelivery(ref, { id: "delivery-1", outcome: "delivered", adapterId: "a" }, "lease", signal)
    await expect(transport.claimDelivery(manager, "lease", "a", signal)).resolves.toEqual({
      id: "manager-delivery",
      ownerKind: "manager",
      conversationId: "D1",
      threadTs: null,
      payloadJson: '{"text":"reply"}',
    })
    await expect(transport.claimUncertainDelivery(manager, "lease", "a", signal)).resolves.toMatchObject({ id: "manager-uncertain", ownerKind: "manager" })
    await transport.ackDelivery(manager, { id: "manager-uncertain", outcome: "uncertain", adapterId: "a" }, "lease", signal)

    expect(calls.map((call) => call.url)).toEqual([
      "http://localhost/api/slack-adapter/leases/targets",
      "http://localhost/api/slack-adapter/leases/acquire",
      "http://localhost/api/slack-adapter/leases/acquire",
      "http://localhost/api/slack-adapter/leases/acquire",
      "http://localhost/api/slack-adapter/leases/renew",
      "http://localhost/api/slack-adapter/leases/hello",
      "http://localhost/api/projects/p/slack-connections/c/ingress",
      "http://localhost/api/slack-manager/ingress",
      "http://localhost/api/projects/p/slack-connections/c/interactions",
      "http://localhost/api/projects/p/slack-connections/c/deliveries/claim",
      "http://localhost/api/projects/p/slack-connections/c/deliveries/ack",
      "http://localhost/api/slack-manager/adapter/e/deliveries/claim",
      "http://localhost/api/slack-manager/adapter/e/deliveries/claim-uncertain",
      "http://localhost/api/slack-manager/adapter/e/deliveries/ack",
    ])
    expect(calls.map((call) => call.method)).toEqual([
      "GET", "POST", "POST", "POST", "POST", "POST", "POST", "POST", "POST", "POST", "POST", "POST", "POST", "POST",
    ])
    expect(calls.every((call) => call.operatorToken === "operator")).toBe(true)
    expect(calls.every((call) => call.operatorId === "operator-id")).toBe(true)
    expect(calls.every((call) => call.operatorToken !== null && call.operatorId !== null)).toBe(true)
    expect(JSON.parse(calls[1]!.body)).toEqual({
      kind: "validation",
      target: { kind: "connection", projectId: "p", connectionId: "c" },
      adapterId: "a",
    })
    expect(JSON.parse(calls[2]!.body)).toEqual({
      kind: "runtime",
      target: { kind: "connection", projectId: "p", connectionId: "c" },
      adapterId: "a",
    })
    expect(JSON.parse(calls[3]!.body)).toEqual({
      kind: "runtime",
      target: { kind: "manager", enrollmentId: "e", workspaceTeamId: "T_MANAGER" },
      adapterId: "a",
    })
    expect(JSON.parse(calls[4]!.body)).toEqual({
      target: { kind: "connection", projectId: "p", connectionId: "c" },
      leaseId: "lease",
      adapterId: "a",
    })
    expect(JSON.parse(calls[5]!.body)).toEqual({
      target: { kind: "connection", projectId: "p", connectionId: "c" },
      leaseId: "lease",
      appId: "A1",
    })
    expect(JSON.parse(calls[6]!.body)).toEqual({ ...envelope, leaseId: "lease", adapterId: "a" })
    expect(JSON.parse(calls[7]!.body)).toEqual({
      appId: "A1",
      workspaceTeamId: "T_MANAGER",
      conversationId: "D",
      messageTs: "1",
      senderSlackUserId: "U",
      text: "task",
      isDirectMessage: true,
      threadTs: null,
      leaseId: "lease",
      adapterId: "a",
    })
    expect(JSON.parse(calls[8]!.body)).toEqual({ ...interaction, leaseId: "lease", adapterId: "a" })
    expect(JSON.parse(calls[9]!.body)).toEqual({ leaseId: "lease", adapterId: "a" })
    expect(JSON.parse(calls[10]!.body)).toEqual({ id: "delivery-1", outcome: "delivered", adapterId: "a", leaseId: "lease" })
    expect(JSON.parse(calls[11]!.body)).toEqual({ leaseId: "lease", adapterId: "a" })
    expect(JSON.parse(calls[12]!.body)).toEqual({ leaseId: "lease", adapterId: "a" })
    expect(JSON.parse(calls[13]!.body)).toEqual({ id: "manager-uncertain", outcome: "uncertain", adapterId: "a", leaseId: "lease" })
  })

  it("maps lease conflicts to null and hello outcomes to typed results", async () => {
    const responses: Array<{ status: number; body: unknown }> = []
    const transport = new HttpAdapterTransport({
      serverUrl: "http://localhost",
      operatorToken: "operator",
      operatorId: "operator-id",
      fetch: async () => {
        const next = responses.shift()!
        return new Response(JSON.stringify(next.body), { status: next.status })
      },
    })
    const ref = { projectId: "p", connectionId: "c" }
    const signal = new AbortController().signal

    responses.push({ status: 409, body: { success: false, error: "no lease right now", code: "lease_not_acquirable" } })
    await expect(transport.acquireLease(ref, "runtime", "a", signal)).resolves.toBeNull()

    responses.push({ status: 409, body: { success: false, error: "lease is stale", code: "lease_stale_or_expired" } })
    await expect(transport.renewLease(ref, "lease", "a", signal)).resolves.toBeNull()

    responses.push({ status: 409, body: { success: false, error: "app id mismatch", code: "app_id_mismatch" } })
    await expect(transport.reportHello(ref, "lease", "A1", signal)).resolves.toBe("app_id_mismatch")

    responses.push({ status: 409, body: { success: false, error: "lease is stale", code: "lease_stale_or_expired" } })
    await expect(transport.reportHello(ref, "lease", "A1", signal)).resolves.toBe("lease_stale_or_expired")

    responses.push({ status: 200, body: { success: true, data: null } })
    await expect(transport.claimDelivery(ref, "lease", "a", signal)).resolves.toBeNull()

    responses.push({ status: 409, body: { success: false, error: "other conflict", code: "delivery_claimed_by_another" } })
    await expect(transport.claimDelivery(ref, "lease", "a", signal)).rejects.toThrow("delivery_claimed_by_another")

    responses.push({ status: 409, body: { success: false, error: "lease is stale", code: "lease_stale_or_expired" } })
    await expect(transport.claimDelivery(ref, "lease", "a", signal)).rejects.toBeInstanceOf(LeaseStaleError)

    responses.push({ status: 409, body: { success: false, error: "lease is stale", code: "lease_stale_or_expired" } })
    await expect(transport.ingress(ref, envelope, "lease", "a", signal)).rejects.toBeInstanceOf(LeaseStaleError)
  })

  it("rejects malformed discovery targets and lease responses without leaking payloads", async () => {
    const transport = new HttpAdapterTransport({
      serverUrl: "http://localhost",
      operatorToken: "operator",
      operatorId: "operator-id",
      fetch: async () => new Response(JSON.stringify({ success: true, data: [{ kind: "manager" }] }), { status: 200 }),
    })
    await expect(transport.discover(new AbortController().signal)).rejects.toThrow("invalid Manager target")

    const transport2 = new HttpAdapterTransport({
      serverUrl: "http://localhost",
      operatorToken: "operator",
      operatorId: "operator-id",
      fetch: async () => new Response(JSON.stringify({ success: true, data: { leaseId: "x" } }), { status: 200 }),
    })
    await expect(transport2.acquireLease({ projectId: "p", connectionId: "c" }, "runtime", "a", new AbortController().signal))
      .rejects.toThrow("invalid lease response")

    const transport3 = new HttpAdapterTransport({
      serverUrl: "http://localhost",
      operatorToken: "operator",
      operatorId: "operator-id",
      fetch: async () => new Response(JSON.stringify({ success: true, data: { id: "x" } }), { status: 200 }),
    })
    await expect(transport3.claimDelivery({ projectId: "p", connectionId: "c" }, "lease", "a", new AbortController().signal))
      .rejects.toThrow("invalid delivery")
  })

  it("rejects non-loopback targets and hides Server error bodies", async () => {
    expect(() => new HttpAdapterTransport({
      serverUrl: "https://127.operator-token.example.test",
      operatorToken: "operator",
      operatorId: "operator-id",
    })).toThrow("loopback")

    const transport = new HttpAdapterTransport({
      serverUrl: "http://127.0.0.1:3456",
      operatorToken: "operator",
      operatorId: "operator-id",
      fetch: async () => new Response("xapp-server-error-secret", { status: 500 }),
    })
    const error = await transport.discover(new AbortController().signal).catch((reason: unknown) => String(reason))
    expect(error).toContain("500")
    expect(error).not.toContain("xapp-server-error-secret")
  })

  it("sends the operator identity header alongside the token on every lease request", async () => {
    const leaseHeaders: Array<{ url: string; token: string | null; id: string | null }> = []
    const transport = new HttpAdapterTransport({
      serverUrl: "http://127.0.0.1:3456",
      operatorToken: "shared-token",
      operatorId: "mohist-slack",
      fetch: async (input, init) => {
        const headers = new Headers(init?.headers)
        leaseHeaders.push({
          url: String(input),
          token: headers.get("x-mohist-operator-token"),
          id: headers.get("x-mohist-operator-id"),
        })
        const url = String(input)
        const data = url.endsWith("/api/slack-adapter/leases/targets")
          ? [{ kind: "connection", projectId: "p", connectionId: "c", expectedAppId: "A1" }]
          : url.endsWith("/leases/acquire")
            ? { leaseId: "lease", generation: 1, expiresAt: "2026-01-01T00:05:00Z", appToken: "xapp", botToken: "xoxb" }
            : url.endsWith("/leases/hello") ? { outcome: "verified" }
              : url.endsWith("/leases/renew")
                ? { leaseId: "lease", kind: "runtime", generation: 2, expiresAt: "2026-01-01T00:10:00Z" }
                : null
        return new Response(JSON.stringify({ success: true, data }), { status: 200 })
      },
    })
    const ref = { projectId: "p", connectionId: "c" }
    const signal = new AbortController().signal
    await transport.discover(signal)
    await transport.acquireLease(ref, "runtime", "a", signal)
    await transport.reportHello(ref, "lease", "A1", signal)
    await transport.renewLease(ref, "lease", "a", signal)

    expect(leaseHeaders.map((headers) => headers.url)).toEqual([
      "http://127.0.0.1:3456/api/slack-adapter/leases/targets",
      "http://127.0.0.1:3456/api/slack-adapter/leases/acquire",
      "http://127.0.0.1:3456/api/slack-adapter/leases/hello",
      "http://127.0.0.1:3456/api/slack-adapter/leases/renew",
    ])
    expect(leaseHeaders.every((headers) => headers.token === "shared-token")).toBe(true)
    expect(leaseHeaders.every((headers) => headers.id === "mohist-slack")).toBe(true)
  })
})
