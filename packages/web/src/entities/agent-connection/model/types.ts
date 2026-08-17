export interface AgentExecutabilityFixEntryPoint {
  label: string
  path: string
  command: string
}

export interface AgentExecutabilityGap {
  code: string
  message: string
  nextAction: string
  fixEntryPoint: AgentExecutabilityFixEntryPoint
}

export interface AgentExecutabilityResult {
  state: string
  gaps: AgentExecutabilityGap[]
  pendingLaunchNote?: string | null
}

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
  offlineGapAt: string | null
}

export interface ConnectionDiagnostic {
  primaryState: string
  reason: string
  nextAction: string
  facts: ConnectionDiagnosticFacts
  executability?: AgentExecutabilityResult | null
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
  executability?: AgentExecutabilityResult | null
  ownerSlackUserId: string | null
  accessPolicy: string
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

export interface AgentConnectionDetailResponse {
  connection: AgentConnectionDto
  botName: string
  appDescription: string
  slackAppCreationReference: string
  managedApp?: ManagedSlackAppProjection | null
}

export interface ManagedSlackAppProjection {
  id: string
  enrollmentId: string
  agentConnectionId: string
  workspaceTeamId: string
  appId: string
  botUserId: string
  appLifecycle: string
  authorization: string
  manifestState: string
  transportKind: string
  transportReadiness: string
  nextAction: string
  bindingState: string
  unknownOutcome: string | null
  errorClass: string | null
  deletedAt: string | null
}

export interface AgentConnectionConfigureRequest {
  appToken: string
  botToken: string
}

export interface AgentConnectionClaimOwnerResponse {
  code: string
  expiresAt: string
}

export const ACCESS_POLICY_VALUES = ['owner_only', 'allowlist', 'anyone'] as const
export type AccessPolicyKind = (typeof ACCESS_POLICY_VALUES)[number]

export interface AccessPolicyState {
  accessPolicy: string
  allowMembers: string[]
  anyoneDisclosure: string
}

export interface AccessPolicyManageRequest {
  accessPolicy: AccessPolicyKind
  allowMembers: string[]
}

export interface AccessPolicyManageResponse {
  connection: AgentConnectionDto
  accessPolicy: string
  allowMembers: string[]
  anyoneDisclosure: string
}

export interface SlackMemberSearchEntry {
  slackUserId: string
  displayName: string | null
  avatarUrl: string | null
}

export interface SlackMemberSearchResponse {
  members: SlackMemberSearchEntry[]
}

export interface SlackOutboxEntry {
  id: string
  projectId: string
  connectionId: string
  workspaceTeamId: string
  conversationId: string
  threadTs: string | null
  kind: string
  state: string
  dispatchRef: string | null
  payloadJson: string
  attemptCount: number
  nextAttemptAt: string | null
  claimedAt: string | null
  claimedByAdapterId: string | null
  deliveredAt: string | null
  deliveryUncertainAt: string | null
  deadLetteredAt: string | null
  lastError: string | null
  createdAt: string
  updatedAt: string
}

export interface SlackOutboxListResponse {
  entries: SlackOutboxEntry[]
}

export interface SlackOutboxResendResponse {
  id: string
  state: string
}
