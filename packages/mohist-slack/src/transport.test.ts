import { describe, expect, it } from "vitest"
import { HttpAdapterTransport } from "./transport.js"

describe("HttpAdapterTransport", () => {
  it("discovers connections and uses the authenticated Connection boundary for lease, ingress, interactions, claim, and ack", async () => {
    const calls: Array<{ url: string; body: string }> = []
    const transport = new HttpAdapterTransport({
      serverUrl: "http://server/",
      operatorToken: "operator",
      fetch: async (input, init) => {
        calls.push({ url: String(input), body: String(init?.body) })
        const route = String(input).split("/").pop()
        const data = route === "adapter-session"
          ? { adapterId: "a", appToken: "xapp", botToken: "xoxb" }
          : route === "adapter" ? [{ projectId: "p", connectionId: "c" }]
          : route === "deliveries" ? null : route === "ingress" ? { kind: "accepted" } : { id: "delivery-1", outcome: "delivered" }
        return new Response(JSON.stringify({ success: true, data }), { status: 200 })
      },
    })
    const ref = { projectId: "p", connectionId: "c" }
    const signal = new AbortController().signal
    await expect(transport.discoverConnections(signal)).resolves.toEqual([ref])
    await expect(transport.lease(ref, "a", signal)).resolves.toMatchObject({ appToken: "xapp" })
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
    expect(calls.map((call) => call.url)).toEqual([
      "http://server/api/slack-connections/adapter",
      "http://server/api/projects/p/slack-connections/c/adapter-session",
      "http://server/api/projects/p/slack-connections/c/ingress",
      "http://server/api/projects/p/slack-connections/c/interactions",
      "http://server/api/projects/p/slack-connections/c/deliveries/claim",
      "http://server/api/projects/p/slack-connections/c/deliveries/ack",
    ])
    expect(JSON.parse(calls[3]!.body)).toMatchObject({ actionId: "mohist_stop_turn", actionValue: "server-signed-value" })
    expect(JSON.parse(calls[5]!.body)).toMatchObject({ id: "delivery-1", outcome: "delivered", adapterId: "a" })
  })
})
