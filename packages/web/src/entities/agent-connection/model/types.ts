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
