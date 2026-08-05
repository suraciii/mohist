import { describe, expect, it } from "vitest"
import { HttpAdapterTransport } from "./transport.js"

describe("HttpAdapterTransport", () => {
  it("uses the operator-authenticated loopback lease contract for discovery and runtime work", async () => {
    const calls: Array<{ url: string; body: string; operatorToken: string | null }> = []
    const transport = new HttpAdapterTransport({
      serverUrl: "http://localhost/",
      operatorToken: "operator",
      fetch: async (input, init) => {
        const url = String(input)
        calls.push({
          url,
          body: String(init?.body),
          operatorToken: new Headers(init?.headers).get("x-mohist-operator-token"),
        })
        const data = url.endsWith("/api/slack-connections/adapter")
          ? [{ ownerKind: "connection", projectId: "p", connectionId: "c", appToken: "xapp-discovery-secret" }]
          : url.endsWith("/api/slack-manager/adapter")
            ? [{ ownerKind: "manager", enrollmentId: "e", workspaceTeamId: "T_MANAGER" }]
            : url.endsWith("/leases/acquire")
              ? { kind: "runtime", leaseId: "lease", appToken: "xapp", botToken: "xoxb" }
              : url.endsWith("/ingress") ? { kind: "accepted" }
                : url.endsWith("/interactions") ? { state: "stop_requested" }
                  : url.endsWith("/claim-uncertain")
                    ? { id: "manager-uncertain", ownerKind: "manager", conversationId: "D_MANAGER", threadTs: null, payloadJson: '{"text":"uncertain"}' }
                    : url.endsWith("/claim")
                      ? { id: url.includes("/slack-manager/") ? "manager-delivery" : "delivery-1", conversationId: "D1", threadTs: null, payloadJson: '{"text":"reply"}' }
                      : null
        return new Response(JSON.stringify({ success: true, data }), { status: 200 })
      },
    })
    const ref = { projectId: "p", connectionId: "c" }
    const manager = { ownerKind: "manager" as const, enrollmentId: "e", workspaceTeamId: "T_MANAGER" }
    const signal = new AbortController().signal
    const discovered = await transport.discover(signal)
    expect(discovered).toEqual([ref, manager])
    expect(JSON.stringify(discovered)).not.toContain("xapp-discovery-secret")
    await expect(transport.acquireLease(ref, "a", signal)).resolves.toMatchObject({ kind: "runtime", leaseId: "lease" })
    await expect(transport.acquireLease(manager, "a", signal)).resolves.toMatchObject({ kind: "runtime", leaseId: "lease" })
    await transport.reportHello(ref, "lease", "A1", signal)
    await transport.ingress(ref, "lease", {
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
    }, signal)
    await transport.interaction(ref, "lease", {
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
    }, signal)
    await transport.claimDelivery(ref, "lease", "a", signal)
    await transport.ackDelivery(ref, "lease", { id: "delivery-1", outcome: "delivered", adapterId: "a" }, signal)
    await transport.claimDelivery(manager, "lease", "a", signal)
    await transport.claimUncertainDelivery(manager, "lease", "a", signal)
    await transport.ackDelivery(manager, "lease", { id: "manager-uncertain", outcome: "uncertain", adapterId: "a" }, signal)
    expect(calls.map((call) => call.url)).toEqual([
      "http://localhost/api/slack-connections/adapter",
      "http://localhost/api/slack-manager/adapter",
      "http://localhost/api/projects/p/slack-connections/c/adapter/leases/acquire",
      "http://localhost/api/slack-manager/adapter/e/leases/acquire",
      "http://localhost/api/projects/p/slack-connections/c/adapter/leases/lease/hello",
      "http://localhost/api/projects/p/slack-connections/c/adapter/leases/lease/ingress",
      "http://localhost/api/projects/p/slack-connections/c/adapter/leases/lease/interactions",
      "http://localhost/api/projects/p/slack-connections/c/adapter/leases/lease/deliveries/claim",
      "http://localhost/api/projects/p/slack-connections/c/adapter/leases/lease/deliveries/ack",
      "http://localhost/api/slack-manager/adapter/e/leases/lease/deliveries/claim",
      "http://localhost/api/slack-manager/adapter/e/leases/lease/deliveries/claim-uncertain",
      "http://localhost/api/slack-manager/adapter/e/leases/lease/deliveries/ack",
    ])
    expect(calls.every((call) => call.operatorToken === "operator")).toBe(true)
    expect(JSON.parse(calls[4]!.body)).toEqual({ appId: "A1" })
    expect(JSON.parse(calls[6]!.body)).toMatchObject({ actionId: "mohist_stop_turn", actionValue: "server-signed-value" })
    expect(JSON.parse(calls[8]!.body)).toMatchObject({ id: "delivery-1", outcome: "delivered", adapterId: "a" })
  })

  it("rejects non-loopback targets and hides Server error bodies", async () => {
    expect(() => new HttpAdapterTransport({
      serverUrl: "https://127.operator-token.example.test",
      operatorToken: "operator",
    })).toThrow("loopback")

    const transport = new HttpAdapterTransport({
      serverUrl: "http://127.0.0.1:3456",
      operatorToken: "operator",
      fetch: async () => new Response("xapp-server-error-secret", { status: 500 }),
    })
    const error = await transport.discover(new AbortController().signal).catch((reason: unknown) => String(reason))
    expect(error).toContain("500")
    expect(error).not.toContain("xapp-server-error-secret")
  })
})
