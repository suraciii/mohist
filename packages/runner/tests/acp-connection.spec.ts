import { describe, expect, it } from "vitest"
import type { RequestPermissionRequest, RequestPermissionResponse, SessionNotification } from "@agentclientprotocol/sdk"

describe("shared ACP connection session routing", () => {
  it("SessionUpdates_RoutedByAcpSessionId", async () => {
    const router = createHandlerRouter()
    const received: string[] = []

    router.setSessionHandlers(
      "session-a",
      async () => { received.push("a") },
      async () => ({ outcome: { outcome: "cancelled" } }),
    )
    router.setSessionHandlers(
      "session-b",
      async () => { received.push("b") },
      async () => ({ outcome: { outcome: "cancelled" } }),
    )

    await router.sessionUpdate({ sessionId: "session-b", update: { sessionUpdate: "agent_message_chunk" } } as never)
    await router.sessionUpdate({ sessionId: "session-a", update: { sessionUpdate: "agent_message_chunk" } } as never)

    expect(received).toEqual(["b", "a"])
  })

  it("RequestPermission_RoutedByAcpSessionId", async () => {
    const router = createHandlerRouter()

    router.setSessionHandlers(
      "session-a",
      async () => {},
      async () => ({ outcome: { outcome: "cancelled" } }),
    )
    router.setSessionHandlers(
      "session-b",
      async () => {},
      async () => ({ outcome: { outcome: "selected", optionId: "allow-b" } }),
    )

    const result = await router.requestPermission({ sessionId: "session-b" } as never)

    expect(result).toEqual({ outcome: { outcome: "selected", optionId: "allow-b" } })
  })
})

type SessionUpdateHandler = (notification: SessionNotification) => Promise<void>
type PermissionHandler = (params: RequestPermissionRequest) => Promise<RequestPermissionResponse>

function createHandlerRouter() {
  const sessionUpdateHandlers = new Map<string, SessionUpdateHandler>()
  const permissionHandlers = new Map<string, PermissionHandler>()

  return {
    setSessionHandlers(sessionId: string, sessionUpdate: SessionUpdateHandler, permission: PermissionHandler) {
      sessionUpdateHandlers.set(sessionId, sessionUpdate)
      permissionHandlers.set(sessionId, permission)
    },
    clearSessionHandlers(sessionId: string) {
      sessionUpdateHandlers.delete(sessionId)
      permissionHandlers.delete(sessionId)
    },
    async sessionUpdate(notification: SessionNotification) {
      await (sessionUpdateHandlers.get(notification.sessionId) ?? (async () => {}))(notification)
    },
    async requestPermission(params: RequestPermissionRequest) {
      return await (permissionHandlers.get(params.sessionId) ?? (async () => ({ outcome: { outcome: "cancelled" } } as RequestPermissionResponse)))(params)
    },
  }
}
