import { describe, expect, it } from "vitest"
import { HttpAdapterTransport } from "./transport.js"

describe("HttpAdapterTransport", () => {
  it("discovers connections and uses the authenticated Connection boundary for lease, ingress, claim, and ack", async () => {
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
    await transport.ingress(ref, { eventType: "message", isDirectMessage: true, teamId: "T", conversationId: "D", messageTs: "1", senderSlackUserId: "U", text: "task" }, signal)
    await transport.claimDelivery(ref, "a", signal)
    await transport.ackDelivery(ref, { id: "delivery-1", outcome: "delivered" }, signal)
    expect(calls.map((call) => call.url)).toEqual([
      "http://server/api/slack-connections/adapter",
      "http://server/api/projects/p/slack-connections/c/adapter-session",
      "http://server/api/projects/p/slack-connections/c/ingress",
      "http://server/api/projects/p/slack-connections/c/deliveries/claim",
      "http://server/api/projects/p/slack-connections/c/deliveries/ack",
    ])
  })
})
