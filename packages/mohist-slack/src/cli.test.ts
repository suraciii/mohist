import { describe, expect, it } from "vitest"
import type { FetchFunction } from "@slack/web-api"
import { createSlackAdapter, resolveOperatorToken } from "./cli.js"

const directToken = "direct-token"

describe("mohist-slack CLI composition", () => {
  it("prefers a direct Mohist token over the compatibility environment variable and file path", async () => {
    const token = await resolveOperatorToken(
      {
        MOHIST_OPERATOR_TOKEN: `  ${directToken}  `,
        OPERATOR_TOKEN: "compatibility-operator-token-0123456789abcdef",
        MOHIST_OPERATOR_TOKEN_PATH: "/run/credentials/operator-token",
      },
      async () => {
        throw new Error("file reader should not be called")
      },
    )

    expect(token).toBe(directToken)
  })

  it("reads, trims, and validates a protected credential path when no direct token is present", async () => {
    const token = await resolveOperatorToken(
      { MOHIST_OPERATOR_TOKEN_PATH: "/run/credentials/operator-token" },
      async (path) => {
        expect(path).toBe("/run/credentials/operator-token")
        return `\n${directToken}\n`
      },
    )

    expect(token).toBe(directToken)
  })

  it("rejects an absent credential path without exposing details", async () => {
    await expect(resolveOperatorToken({}, async () => "should not be read"))
      .rejects.toThrow("Mohist operator credential is required")
  })

  it("rejects a blank credential path without exposing details", async () => {
    await expect(resolveOperatorToken(
      { MOHIST_OPERATOR_TOKEN_PATH: "   " },
      async () => "should not be read",
    )).rejects.toThrow("Mohist operator credential is required")
  })

  it("rejects an unreadable credential file without exposing the filesystem error", async () => {
    const result = await resolveOperatorToken(
      { MOHIST_OPERATOR_TOKEN_PATH: "/run/credentials/operator-token" },
      async () => { throw new Error("sensitive filesystem detail") },
    ).catch((error: unknown) => error as Error)

    expect(result.message).toBe("Mohist operator credential file could not be read")
    expect(result.message).not.toContain("sensitive filesystem detail")
  })

  it("rejects a blank credential file without exposing its contents", async () => {
    const result = await resolveOperatorToken(
      { MOHIST_OPERATOR_TOKEN_PATH: "/run/credentials/operator-token" },
      async () => " \n\t",
    ).catch((error: unknown) => error as Error)

    expect(result.message).toBe("Mohist operator credential is invalid")
  })

  it("delivers a Manager message through the production WebClient and acknowledges it on the Manager route", async () => {
    const managerCredential = "test-manager-credential"
    const manager = { ownerKind: "manager" as const, enrollmentId: "manager-enrollment", workspaceTeamId: "T_MANAGER" }
    const delivery = {
      id: "manager-delivery",
      ownerKind: "manager" as const,
      conversationId: "D_MANAGER",
      threadTs: null,
      payloadJson: JSON.stringify({ text: "manager reply" }),
    }
    const serverCalls: Array<{ url: string; body?: string; operatorToken: string | null }> = []
    const ackBodies: unknown[] = []
    let deliveryClaimed = false
    const serverFetch: typeof fetch = async (input, init) => {
      const url = String(input)
      const body = init?.body === undefined ? undefined : String(init.body)
      serverCalls.push({
        url,
        body,
        operatorToken: new Headers(init?.headers).get("x-mohist-operator-token"),
      })
      let data: unknown
      if (url.endsWith("/api/slack-connections/adapter")) {
        data = []
      } else if (url.endsWith("/api/slack-manager/adapter")) {
        data = [manager]
      } else if (url.endsWith("/session")) {
        data = { adapterId: "adapter-manager", ownerKind: "manager", workspaceTeamId: manager.workspaceTeamId, botToken: managerCredential }
      } else if (url.endsWith("/deliveries/claim-uncertain")) {
        data = null
      } else if (url.endsWith("/deliveries/claim")) {
        data = deliveryClaimed ? null : delivery
        deliveryClaimed = true
      } else if (url.endsWith("/deliveries/ack")) {
        ackBodies.push(JSON.parse(body ?? "null") as unknown)
        data = null
      } else {
        throw new Error(`Unexpected Server request: ${url}`)
      }
      return new Response(JSON.stringify({ success: true, data }), { status: 200 })
    }
    const slackCalls: Array<{ url: string; authorization: string | null; body: URLSearchParams }> = []
    const slackFetch: FetchFunction = async (input, init) => {
      const body = new URLSearchParams(String(init?.body ?? ""))
      slackCalls.push({
        url: String(input),
        authorization: new Headers(init?.headers).get("authorization"),
        body,
      })
      return new Response(JSON.stringify({ ok: true, ts: "1700000000.001" }), { status: 200 })
    }
    const controller = new AbortController()
    const adapter = createSlackAdapter({
      adapterId: "adapter-manager",
      serverUrl: "http://server",
      operatorToken: "test-operator",
      serverFetch,
      slackFetch,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
      discoveryIntervalMs: 60_000,
    })

    await adapter.start(controller.signal)

    expect(serverCalls.length).toBeGreaterThan(0)
    expect(serverCalls.every((call) => call.operatorToken === "test-operator")).toBe(true)
    expect(slackCalls).toHaveLength(1)
    expect(slackCalls[0]).toMatchObject({
      url: "https://slack.com/api/chat.postMessage",
      authorization: `Bearer ${managerCredential}`,
    })
    expect(slackCalls[0]?.body.get("channel")).toBe("D_MANAGER")
    expect(slackCalls[0]?.body.get("text")).toBe("manager reply")
    expect(ackBodies).toEqual([{
      id: delivery.id,
      outcome: "delivered",
      adapterId: "adapter-manager",
      providerMessageIdentity: { conversationId: "D_MANAGER", messageTs: "1700000000.001" },
    }])
    expect(serverCalls.some((call) => call.url.endsWith("/api/slack-manager/adapter/manager-enrollment/deliveries/ack"))).toBe(true)
    controller.abort()
  })
})
