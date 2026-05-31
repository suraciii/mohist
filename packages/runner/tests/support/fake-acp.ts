import { AgentSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { Agent, Stream } from "@agentclientprotocol/sdk"
import type { AcpProcessHandle } from "../../src/actions/acp-agent.js"

export function fakeAcpProcess(onPrompt?: () => Promise<void>): AcpProcessHandle {
  const agent = new FakeAgent(onPrompt)
  const [clientStream, agentStream] = linkedStreams()
  const connection = new AgentSideConnection(() => agent.handler(), agentStream)
  agent.bind(connection)
  return {
    stream: clientStream,
    processPid: 123,
    spawnFailure: new Promise<never>(() => {}),
    exitFailure: new Promise<never>(() => {}),
    markInitialized() {},
    exitCode() { return 0 },
    async cleanup() {},
  }
}

function linkedStreams(): [Stream, Stream] {
  const clientToAgent = new TransformStream()
  const agentToClient = new TransformStream()
  return [
    { writable: clientToAgent.writable, readable: agentToClient.readable },
    { writable: agentToClient.writable, readable: clientToAgent.readable },
  ]
}

class FakeAgent {
  private connection!: AgentSideConnection

  constructor(private readonly onPrompt?: () => Promise<void>) {}

  bind(connection: AgentSideConnection) {
    this.connection = connection
  }

  handler(): Agent {
    const self = this
    return {
      async initialize() {
        return { protocolVersion: PROTOCOL_VERSION, agentInfo: { name: "fake-acp-agent", version: "0.1.0" }, agentCapabilities: {} }
      },
      async newSession() {
        return { sessionId: "fake-session-1" }
      },
      async prompt(params) {
        await self.onPrompt?.()
        await self.connection.sessionUpdate({
          sessionId: params.sessionId,
          update: { sessionUpdate: "agent_message_chunk", content: { type: "text", text: "done" } },
        } as never)
        return { stopReason: "end_turn" }
      },
      async authenticate() { return {} },
    }
  }
}
