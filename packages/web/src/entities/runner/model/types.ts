export type RunnerPresenceState = 'online' | 'stale' | 'offline'
export type RunnerControlState = 'connected' | 'disconnected'
export type RunnerAdmissionState = 'ready' | 'blocked'
export type RunnerRuntimeReadinessState = 'ready' | 'not-ready'

export interface RunnerIdentity {
  id: string
  hostname: string | null
  kind: string | null
  component: string | null
  sourceRevision: string | null
  releaseId: string | null
  generation: number | null
}

export interface RunnerPresence {
  state: RunnerPresenceState
  lastObservedAt: string | null
}

export interface RunnerControl {
  state: RunnerControlState
  generation: string | null
}

export interface RunnerAdmission {
  state: RunnerAdmissionState
  reasonCodes: string[]
}

export interface RunnerRuntimeReadiness {
  state: RunnerRuntimeReadinessState
  generation: number | null
  reasonCode: string | null
}

export interface RunnerRuntimeCatalog {
  complete: boolean | null
  capabilityRevision: string | null
  modelCount: number
  models: string[]
  variants: Record<string, string[]>
  supportsReasoningEffort: boolean | null
  reasoningEfforts: Record<string, string[]>
}

export interface RunnerRuntime {
  name: string
  readiness: RunnerRuntimeReadiness
  catalog: RunnerRuntimeCatalog | null
}

export interface RunnerCapacity {
  used: number | null
  total: number
}

export interface RunnerActiveWorkIssueRef {
  projectId: string
  issueNumber: number
}

export type RunnerOwnerKind = 'workflow' | 'agent-job' | string

export interface RunnerActiveWork {
  workId: string
  ownerKind: RunnerOwnerKind
  ownerId: string
  workType: string
  stage: string | null
  title: string | null
  issue: RunnerActiveWorkIssueRef | null
}

export interface RunnerDrain {
  active: boolean
  kind: 'update' | 'generic' | string
  updateInterruptId: string | null
}

export type RunnerEnvironmentApplicationPhase =
  | 'waiting'
  | 'applying'
  | 'active'
  | 'failed'
  | 'cancelled'
  | 'unconfirmed'
  | 'unknown'

export interface RunnerEnvironmentApplication {
  updateId: string
  targetVersion: string
  previousVersion: string | null
  phase: RunnerEnvironmentApplicationPhase
  failureCode: string | null
  baseProcessGeneration: string | null
  baseConnectionGeneration: string | null
  requestedAt: string
  completedAt: string | null
}

export type RunnerEnvironmentToolCheckOutcome = 'passed' | 'failed' | 'not-found' | 'timed-out' | 'error' | string

export interface RunnerEnvironmentToolCheck {
  executable: string
  resolvedPath: string | null
  snapshotKind: 'active' | 'candidate' | string
  snapshotVersion: string | null
  outcome: RunnerEnvironmentToolCheckOutcome
  exitCode: number | null
  durationMilliseconds: number
  checkedAt: string
}

export interface RunnerEnvironmentObservation {
  processGeneration: string | null
  environmentVersion: string | null
  environmentLoadedAt: string | null
  candidateSource: string | null
  candidateUser: string | null
  candidateVersion: string | null
  candidateVariables: string[]
  candidateCapturedAt: string | null
  candidateAddedVariables: string[]
  candidateRemovedVariables: string[]
  candidateChangedVariables: string[]
  toolChecks: RunnerEnvironmentToolCheck[]
  reportedAt: string
}

export interface RunnerEnvironment {
  activeVersion: string | null
  activeLoadedAt: string | null
  application: RunnerEnvironmentApplication | null
  observation?: RunnerEnvironmentObservation | null
}

export interface RunnerNextAction {
  code: string
  message: string
  command: string | null
}

export interface RunnerStatusEntry {
  identity: RunnerIdentity
  presence: RunnerPresence
  control: RunnerControl
  admission: RunnerAdmission
  capabilities: string[]
  runtimes: RunnerRuntime[]
  capacity: RunnerCapacity | null
  activeWorks: RunnerActiveWork[]
  drain: RunnerDrain | null
  nextActions: RunnerNextAction[]
  environment?: RunnerEnvironment | null
}

/** Canonical row name used by the Web Runner surfaces. */
export type RunnerStatusRow = RunnerStatusEntry

export interface RunnerInventory {
  state: 'first-install' | 'ready' | string
  nextActions: RunnerNextAction[]
}

export interface RunnerStatusListResponse {
  observedAt: string
  inventory: RunnerInventory
  runners: RunnerStatusEntry[]
}

export interface RunnerStatusDetailResponse {
  observedAt: string
  runner: RunnerStatusEntry
}

export interface RunnerStatusSummary {
  readyCount: number
  blockedCount: number
  onlineCount: number
  staleCount: number
  offlineCount: number
  disconnectedCount: number
  drainingCount: number
  fullCount: number
  activeWorkCount: number
  capacityUsed: number | null
  capacityTotal: number
  hasUnknownCapacity: boolean
  hasAdmissibleCapacity: boolean
  rows: RunnerStatusEntry[]
  inventory: RunnerInventory | null
  isLoading?: boolean
  isError?: boolean
}
