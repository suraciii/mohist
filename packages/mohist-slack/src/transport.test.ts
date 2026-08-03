import { describe, expect, it } from "vitest"
import { HttpAdapterTransport } from "./transport.js"

describe("HttpAdapterTransport", () => {
  it("keeps Manager discovery and delivery on its explicit adapter boundary", async () => {
    const calls: Array<{ url: string; body: string }> = []
    const transport = new HttpAdapterTransport({
      serverUrl: "http://server/",
      operatorToken: "operator",
      fetch: async (input, init) => {
        calls.push({ url: String(input), body: String(init?.body) })
        const url = String(input)
        const data = url.endsWith("/api/slack-connections/adapter")
          ? [{ ownerKind: "connection", projectId: "p", connectionId: "c" }]
          : url.endsWith("/api/slack-manager/adapter")
            ? [{ ownerKind: "manager", enrollmentId: "e", workspaceTeamId: "T_MANAGER" }]
            : url.includes("/api/projects/") && url.endsWith("/adapter-session")
          ? { adapterId: "a", appToken: "xapp", botToken: "xoxb" }
          : url.includes("/api/slack-manager/adapter/") && url.endsWith("/session")
            ? { adapterId: "a", ownerKind: "manager", workspaceTeamId: "T_MANAGER" }
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
    const ref = { ownerKind: "connection" as const, projectId: "p", connectionId: "c" }
    const manager = { ownerKind: "manager" as const, enrollmentId: "e", workspaceTeamId: "T_MANAGER" }
    const signal = new AbortController().signal
    await expect(transport.discoverConnections(signal)).resolves.toEqual([ref, manager])
    await expect(transport.lease(ref, "a", signal)).resolves.toMatchObject({ appToken: "xapp" })
    await expect(transport.lease(manager, "a", signal)).resolves.toMatchObject({ ownerKind: "manager" })
    await transport.ingress(ref, {
      eventType: "message",
      isDirectMessage: true,
      teamId: "T",
      conversationId: "D",
      messageTs: "1",
      threadTs: null,
      mentionedUserIds: [],
      senderSlackUserId: "U",
      senderKind: "human",
      text: "task",
    }, signal)
    await transport.interaction(ref, {
      eventType: "block_actions",
      interactionId: "interaction-1",
      teamId: "T",
      conversationId: "D",
      messageTs: "2",
      threadTs: null,
      actorSlackUserId: "U",
      actionId: "mohist_stop_turn",
      actionValue: "server-signed-value",
    }, signal)
    await transport.claimDelivery(ref, "a", signal)
    await transport.ackDelivery(ref, { id: "delivery-1", outcome: "delivered", adapterId: "a" }, signal)
    await transport.claimDelivery(manager, "a", signal)
    await transport.claimUncertainDelivery(manager, "a", signal)
    await transport.ackDelivery(manager, { id: "manager-uncertain", outcome: "uncertain", adapterId: "a" }, signal)
    expect(calls.map((call) => call.url)).toEqual([
      "http://server/api/slack-connections/adapter",
      "http://server/api/slack-manager/adapter",
      "http://server/api/projects/p/slack-connections/c/adapter-session",
      "http://server/api/slack-manager/adapter/e/session",
      "http://server/api/projects/p/slack-connections/c/ingress",
      "http://server/api/projects/p/slack-connections/c/interactions",
      "http://server/api/projects/p/slack-connections/c/deliveries/claim",
      "http://server/api/projects/p/slack-connections/c/deliveries/ack",
      "http://server/api/slack-manager/adapter/e/deliveries/claim",
      "http://server/api/slack-manager/adapter/e/deliveries/claim-uncertain",
      "http://server/api/slack-manager/adapter/e/deliveries/ack",
    ])
    expect(JSON.parse(calls[5]!.body)).toMatchObject({ actionId: "mohist_stop_turn", actionValue: "server-signed-value" })
    expect(JSON.parse(calls[7]!.body)).toMatchObject({ id: "delivery-1", outcome: "delivered", adapterId: "a" })
    expect(JSON.parse(calls[10]!.body)).toMatchObject({ id: "manager-uncertain", outcome: "uncertain", adapterId: "a" })
  })
})
