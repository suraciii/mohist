import { describe, expect, it } from "vitest"
import type { RequestPermissionRequest, RequestPermissionResponse, SessionNotification } from "@agentclientprotocol/sdk"
import { AcpSessionManager, type SessionTarget } from "../src/runtime/acp-connection.js"

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

describe("AcpSessionManager keys", () => {
  it("WorkflowTarget_KeyHasWorkflowPrefix", () => {
    const manager = new AcpSessionManager()
    const target: SessionTarget = { kind: "workflow", projectId: "project-1", workflowRunId: "wf-1", sessionName: "build" }
    expect(manager.key(target)).toBe("workflow:wf-1:build")
  })

  it("GenericTarget_KeyHasGenericPrefix", () => {
    const manager = new AcpSessionManager()
    const target: SessionTarget = { kind: "generic", projectId: "project-1", sessionId: "session-abc" }
    expect(manager.key(target)).toBe("generic:session-abc")
  })

  it("WorkflowKey_Helper_ProducesPrefixedKey", () => {
    const manager = new AcpSessionManager()
    expect(manager.workflowKey("wf-1", "build")).toBe("workflow:wf-1:build")
  })

  it("GenericKey_Helper_ProducesPrefixedKey", () => {
    const manager = new AcpSessionManager()
    expect(manager.genericKey("session-abc")).toBe("generic:session-abc")
  })

  it("WorkflowAndGenericTargets_NeverCollide_EvenIfWorkIdsMatched", () => {
    const manager = new AcpSessionManager()
    const workflowTarget: SessionTarget = { kind: "workflow", projectId: "project-1", workflowRunId: "shared", sessionName: "shared" }
    const genericTarget: SessionTarget = { kind: "generic", projectId: "project-1", sessionId: "shared" }
    const workflowKey = manager.key(workflowTarget)
    const genericKey = manager.key(genericTarget)
    expect(workflowKey).not.toBe(genericKey)
    expect(workflowKey.startsWith("workflow:")).toBe(true)
    expect(genericKey.startsWith("generic:")).toBe(true)

    manager.set(workflowKey, { sessionId: "acp-workflow-1", workDir: "D:/wf" })
    manager.set(genericKey, { sessionId: "acp-generic-1", workDir: "D:/generic" })

    expect(manager.get(workflowKey)?.sessionId).toBe("acp-workflow-1")
    expect(manager.get(genericKey)?.sessionId).toBe("acp-generic-1")
    expect(manager.has(workflowKey)).toBe(true)
    expect(manager.has(genericKey)).toBe(true)

    manager.delete(genericKey)
    expect(manager.has(genericKey)).toBe(false)
    expect(manager.has(workflowKey)).toBe(true)
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
