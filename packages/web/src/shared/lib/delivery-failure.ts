export type DeliveryFailureKind =
  | 'conflict'
  | 'base-moved'
  | 'retry-safe'
  | 'branch-invariant-violation'
  | 'workspace-missing'
  | 'workspace-corrupt'
  | 'workspace-identity-mismatch'
  | 'config-error'
  | 'protection-conflict'
  | 'pr-state-conflict'

export interface DeliveryFailureGuidance {
  failureKind: DeliveryFailureKind
  label: string
  nextAction: string
  retryable: boolean
}

export interface BranchInvariantEvidence {
  expectedBranch: string
  observedBranch: string
  observedRef: string | null
  boundary: 'start' | 'end' | null
}

export interface WorkspaceMaterializationEvidence {
  workspacePath: string | null
  expectedRunId: string | null
  actualRunId: string | null
}

export const DELIVERY_FAILURE_KINDS: readonly DeliveryFailureKind[] = [
  'conflict',
  'base-moved',
  'retry-safe',
  'branch-invariant-violation',
  'workspace-missing',
  'workspace-corrupt',
  'workspace-identity-mismatch',
  'config-error',
  'protection-conflict',
  'pr-state-conflict',
] as const

export const WORKSPACE_MATERIALIZATION_FAILURE_KINDS: readonly DeliveryFailureKind[] = [
  'workspace-missing',
  'workspace-corrupt',
  'workspace-identity-mismatch',
] as const

const DELIVERY_FAILURE_GUIDANCE: Record<DeliveryFailureKind, DeliveryFailureGuidance> = {
  conflict: {
    failureKind: 'conflict',
    label: 'Conflict needs attention',
    nextAction: 'Conflicts could not be resolved automatically. Inspect the conflicting files, resolve them on the issue branch, and rerun prepare.',
    retryable: false,
  },
  'base-moved': {
    failureKind: 'base-moved',
    label: 'Base branch moved',
    nextAction: 'The base branch moved during publish. Prepare the branch again, then publish. The workflow integrate retry will re-fetch and rebase before re-attempting the merge.',
    retryable: true,
  },
  'retry-safe': {
    failureKind: 'retry-safe',
    label: 'Transient failure',
    nextAction: 'Retry the task — the failure is unrelated to conflicts or base movement.',
    retryable: true,
  },
  'branch-invariant-violation': {
    failureKind: 'branch-invariant-violation',
    label: 'Runner / action branch-invariant violation',
    nextAction: 'This is a runner or action bug: the workflow workspace left its expected run branch. Retry the task — the runner will restore the run branch automatically — and report the issue if it recurs. Issue work is not the cause.',
    retryable: true,
  },
  'workspace-missing': {
    failureKind: 'workspace-missing',
    label: 'Workflow workspace materialization failure',
    nextAction: 'The runner could not find the workflow workspace bound to this run. Issue work is not the cause — the workflow-start materialization pipeline must be repaired (rebind the workspace, or investigate the runner\'s workspace root) before this run can continue.',
    retryable: false,
  },
  'workspace-corrupt': {
    failureKind: 'workspace-corrupt',
    label: 'Workflow workspace materialization failure',
    nextAction: 'The runner\'s workflow workspace is unreadable or its workspace marker is missing/corrupt. Issue work is not the cause — re-materialize the workflow workspace at the run\'s bound path before this run can continue.',
    retryable: false,
  },
  'workspace-identity-mismatch': {
    failureKind: 'workspace-identity-mismatch',
    label: 'Workflow workspace materialization failure',
    nextAction: 'The workflow workspace at the run\'s bound path belongs to a different workflow run. Issue work is not the cause — re-bind a fresh workflow workspace to this run before it can continue.',
    retryable: false,
  },
  'config-error': {
    failureKind: 'config-error',
    label: 'Runner environment is misconfigured',
    nextAction: 'Install the GitHub CLI (`gh`) on the runner host and run `gh auth login` to authenticate with GitHub. Then re-run the issue. The workflow will not auto-retry this kind — environment fixes need a human before the next attempt.',
    retryable: false,
  },
  'protection-conflict': {
    failureKind: 'protection-conflict',
    label: 'Branch protection blocked the merge',
    nextAction: 'GitHub rejected the merge because branch protection requires status checks or reviews that this run cannot satisfy. Adjust the repository\'s branch-protection rules (or switch this issue to the `mohist/default` workflow) and re-run. The workflow will not auto-retry this kind.',
    retryable: false,
  },
  'pr-state-conflict': {
    failureKind: 'pr-state-conflict',
    label: 'Pull request state changed externally',
    nextAction: 'The pull request was closed or its state changed outside the runner between workflow steps (for example, by a human via the GitHub UI). Decide whether to re-open the PR or abandon it, then re-run or close the issue. The workflow will not auto-retry this kind.',
    retryable: false,
  },
}

export function isDeliveryFailureKind(value: unknown): value is DeliveryFailureKind {
  return (
    value === 'conflict' ||
    value === 'base-moved' ||
    value === 'retry-safe' ||
    value === 'branch-invariant-violation' ||
    value === 'workspace-missing' ||
    value === 'workspace-corrupt' ||
    value === 'workspace-identity-mismatch' ||
    value === 'config-error' ||
    value === 'protection-conflict' ||
    value === 'pr-state-conflict'
  )
}

export function isWorkspaceMaterializationFailureKind(
  value: unknown,
): value is DeliveryFailureKind {
  return (
    value === 'workspace-missing' ||
    value === 'workspace-corrupt' ||
    value === 'workspace-identity-mismatch'
  )
}

export function getDeliveryFailureGuidance(kind: DeliveryFailureKind): DeliveryFailureGuidance {
  return DELIVERY_FAILURE_GUIDANCE[kind]
}

export interface DeliveryFailureResolution {
  failureKind: DeliveryFailureKind | null
  guidance: DeliveryFailureGuidance | null
  evidence: BranchInvariantEvidence | null
  workspaceEvidence: WorkspaceMaterializationEvidence | null
}

const KIND_IN_MESSAGE =
  /\((conflict|base-moved|retry-safe|branch-invariant-violation|workspace-missing|workspace-corrupt|workspace-identity-mismatch|config-error|protection-conflict|pr-state-conflict)\)/i
const BRANCH_INVARIANT_IN_MESSAGE = /\bbranch-invariant\s+violation\b(?:\s+at\s+(start|end)\s+boundary)?/i
const BRANCH_EVIDENCE_IN_MESSAGE =
  /expected\s+branch\s+'(?<expected>[^']*)'.*?observed\s+(?:'(?<observed>[^']*)'|detached\s+at\s+(?<ref>\S+))/i

export function resolveDeliveryFailureFromOutput(
  output: unknown,
): DeliveryFailureResolution {
  if (output == null) return EMPTY_RESOLUTION
  const candidate = extractFailureKindCandidate(output)
  if (!candidate) return EMPTY_RESOLUTION
  return resolveDeliveryFailure(candidate, output)
}

export function resolveDeliveryFailureFromMessage(
  message: string | null | undefined,
): DeliveryFailureResolution {
  if (!message) return EMPTY_RESOLUTION
  const match = message.match(KIND_IN_MESSAGE)
  if (match) {
    return resolveDeliveryFailure(match[1], message)
  }
  if (BRANCH_INVARIANT_IN_MESSAGE.test(message)) {
    return resolveDeliveryFailure('branch-invariant-violation', message)
  }
  return EMPTY_RESOLUTION
}

const EMPTY_RESOLUTION: DeliveryFailureResolution = {
  failureKind: null,
  guidance: null,
  evidence: null,
  workspaceEvidence: null,
}

function extractFailureKindCandidate(value: unknown): unknown {
  if (value == null) return null
  if (typeof value === 'string') {
    const trimmed = value.trim()
    if (!trimmed) return null
    if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
      try {
        return extractFailureKindCandidate(JSON.parse(trimmed))
      } catch {
        // fall through and try the regex
      }
    }
    const match = trimmed.match(KIND_IN_MESSAGE)
    if (match && isDeliveryFailureKind(match[1])) return match[1]
    if (BRANCH_INVARIANT_IN_MESSAGE.test(trimmed)) return 'branch-invariant-violation'
    return null
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      const found = extractFailureKindCandidate(item)
      if (found != null) return found
    }
    return null
  }
  if (typeof value === 'object') {
    const record = value as Record<string, unknown>
    const direct = record.failureKind ?? record.FailureKind ?? record.errorCode ?? record.ErrorCode
    if (isDeliveryFailureKind(direct)) return direct
    if (isDeliveryFailureKind(record.kind)) {
      const kind = record.kind as string
      if (kind === 'branch-invariant-violation' || isDeliveryFailureKind(kind)) {
        return kind
      }
    }
    if ('output' in record) {
      const fromOutput = extractFailureKindCandidate(record.output)
      if (fromOutput != null) return fromOutput
    }
    if ('branchStability' in record) {
      const fromStack = extractFailureKindCandidate(record.branchStability)
      if (fromStack != null) return fromStack
    }
    if ('message' in record) {
      const fromMessage = extractFailureKindCandidate(record.message)
      if (fromMessage != null) return fromMessage
    }
  }
  return null
}

function resolveDeliveryFailure(
  candidate: unknown,
  evidenceSource: unknown,
): DeliveryFailureResolution {
  if (typeof candidate !== 'string' || !isDeliveryFailureKind(candidate)) {
    return EMPTY_RESOLUTION
  }
  const evidence =
    candidate === 'branch-invariant-violation'
      ? extractBranchInvariantEvidence(evidenceSource)
      : null
  const workspaceEvidence = isWorkspaceMaterializationFailureKind(candidate)
    ? extractWorkspaceMaterializationEvidence(evidenceSource)
    : null
  return {
    failureKind: candidate,
    guidance: DELIVERY_FAILURE_GUIDANCE[candidate],
    evidence,
    workspaceEvidence,
  }
}

export function extractWorkspaceMaterializationEvidence(
  source: unknown,
): WorkspaceMaterializationEvidence | null {
  const node = findWorkspaceEvidenceNode(source)
  if (!node) return null
  const workspacePath = readString(node.workspacePath) ?? null
  const expected = readIdentityNode(node.expected)
  const actual = readIdentityNode(node.actual)
  const expectedRunId = readString(expected?.workflowRunId) ?? null
  const actualRunId = readString(actual?.workflowRunId) ?? null
  if (!workspacePath && !expectedRunId && !actualRunId) return null
  return { workspacePath, expectedRunId, actualRunId }
}

export function extractBranchInvariantEvidence(
  source: unknown,
): BranchInvariantEvidence | null {
  const evidenceNode = findBranchEvidenceNode(source)
  if (evidenceNode) {
    const boundaryRaw = readString(evidenceNode.boundary)
    const boundary = boundaryRaw === 'start' || boundaryRaw === 'end' ? boundaryRaw : null
    const expected = readString(evidenceNode.expectedBranch) ?? ''
    const observed = readString(evidenceNode.observedBranch) ?? ''
    const observedRef = readString(evidenceNode.observedRef) ?? null
    if (expected || observed || observedRef) {
      return { expectedBranch: expected, observedBranch: observed, observedRef, boundary }
    }
  }
  if (typeof source === 'string') {
    return extractBranchInvariantEvidenceFromMessage(source)
  }
  if (source && typeof source === 'object' && 'message' in (source as Record<string, unknown>)) {
    const message = (source as Record<string, unknown>).message
    if (typeof message === 'string') {
      return extractBranchInvariantEvidenceFromMessage(message)
    }
  }
  return null
}

function extractBranchInvariantEvidenceFromMessage(
  message: string,
): BranchInvariantEvidence | null {
  const match = message.match(BRANCH_EVIDENCE_IN_MESSAGE)
  if (!match || !match.groups) return null
  const boundaryMatch = message.match(BRANCH_INVARIANT_IN_MESSAGE)
  const boundary = boundaryMatch?.[1] === 'start' || boundaryMatch?.[1] === 'end' ? (boundaryMatch[1] as 'start' | 'end') : null
  return {
    expectedBranch: match.groups['expected'] ?? '',
    observedBranch: match.groups['observed'] ?? '',
    observedRef: match.groups['ref'] ?? null,
    boundary,
  }
}

function findBranchEvidenceNode(
  value: unknown,
): { expectedBranch?: unknown; observedBranch?: unknown; observedRef?: unknown; boundary?: unknown } | null {
  if (value == null) return null
  if (typeof value === 'string') {
    const trimmed = value.trim()
    if (!trimmed) return null
    if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
      try {
        return findBranchEvidenceNode(JSON.parse(trimmed))
      } catch {
        return null
      }
    }
    return null
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      const found = findBranchEvidenceNode(item)
      if (found) return found
    }
    return null
  }
  if (typeof value === 'object') {
    const record = value as Record<string, unknown>
    if (record['kind'] === 'branch-invariant-violation') {
      return record as {
        expectedBranch?: unknown
        observedBranch?: unknown
        observedRef?: unknown
        boundary?: unknown
      }
    }
    if ('output' in record) {
      const fromOutput = findBranchEvidenceNode(record['output'])
      if (fromOutput) return fromOutput
    }
    if ('branchStability' in record) {
      const fromStack = findBranchEvidenceNode(record['branchStability'])
      if (fromStack) return fromStack
    }
  }
  return null
}

function readString(value: unknown): string | undefined {
  if (typeof value === 'string') return value
  return undefined
}

function findWorkspaceEvidenceNode(value: unknown): {
  workspacePath?: unknown
  expected?: unknown
  actual?: unknown
} | null {
  if (value == null) return null
  if (typeof value === 'string') {
    const trimmed = value.trim()
    if (!trimmed) return null
    if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
      try {
        return findWorkspaceEvidenceNode(JSON.parse(trimmed))
      } catch {
        return null
      }
    }
    return null
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      const found = findWorkspaceEvidenceNode(item)
      if (found) return found
    }
    return null
  }
  if (typeof value === 'object') {
    const record = value as Record<string, unknown>
    if (isWorkspaceMaterializationFailureKind(record['kind'])) {
      return record as {
        workspacePath?: unknown
        expected?: unknown
        actual?: unknown
      }
    }
    if ('output' in record) {
      const fromOutput = findWorkspaceEvidenceNode(record['output'])
      if (fromOutput) return fromOutput
    }
  }
  return null
}

function readIdentityNode(value: unknown): { workflowRunId?: unknown } | null {
  if (!value || typeof value !== 'object') return null
  return value as { workflowRunId?: unknown }
}
