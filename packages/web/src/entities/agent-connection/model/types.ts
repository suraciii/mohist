export interface ConnectionIdentityFacts {
  verificationStatus: string
  verifiedBotName: string | null
  botName: string
  agentName: string | null
  verifiedBotIconUrl: string | null
  avatarHash: string | null
  driftKinds: string[]
}

export interface ConnectionDiagnosticFacts {
  setupProgress: string
  desiredState: string
  connectionHealth: string
  healthReason: string | null
  credentialStatus: string
  adapterOnline: boolean
  ownerAvailability: string
  agentReadiness: string
  identity: ConnectionIdentityFacts
}

export interface ConnectionDiagnostic {
  primaryState: string
  reason: string
  nextAction: string
  facts: ConnectionDiagnosticFacts
}

export interface AgentConnectionDto {
  id: string
  projectId: string
  agentId: string
  providerKind: string
  workspaceTeamId: string
  appId: string
  botUserId: string
  botName: string
  avatarHash: string | null
  verifiedBotName: string | null
  verifiedBotIconUrl: string | null
  setupProgress: string
  desiredState: string
  connectionHealth: string
  healthReason: string | null
  agentReadiness: string
  ownerSlackUserId: string | null
  lastHeartbeatAt: string | null
  createdAt: string
  updatedAt: string
  deletedAt: string | null
}

export interface AgentConnectionCreateRequest {
  agentId: string
  workspaceTeamId?: string
  appId?: string
  botUserId?: string
  botName?: string | null
  avatarHash?: string | null
}

export interface AgentConnectionCreateResponse {
  connection: AgentConnectionDto
  botName: string
  appDescription: string
  slackAppCreationReference: string
}
