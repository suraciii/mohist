export type DeliveryFailureKind =
  | 'conflict'
  | 'base-moved'
  | 'retry-safe'
  | 'branch-invariant-violation'

export interface DeliveryFailureGuidance {
  failureKind: DeliveryFailureKind
  label: string
  nextAction: string
}

export interface BranchInvariantEvidence {
  expectedBranch: string
  observedBranch: string
  observedRef: string | null
  boundary: 'start' | 'end' | null
}

export const DELIVERY_FAILURE_KINDS: readonly DeliveryFailureKind[] = [
  'conflict',
  'base-moved',
  'retry-safe',
  'branch-invariant-violation',
] as const

const DELIVERY_FAILURE_GUIDANCE: Record<DeliveryFailureKind, DeliveryFailureGuidance> = {
  conflict: {
    failureKind: 'conflict',
    label: 'Conflict needs attention',
    nextAction: 'Conflicts could not be resolved automatically. Inspect the conflicting files, resolve them on the issue branch, and rerun prepare.',
  },
  'base-moved': {
    failureKind: 'base-moved',
    label: 'Base branch moved',
    nextAction: 'The base branch moved during publish. Prepare the branch again, then publish.',
  },
  'retry-safe': {
    failureKind: 'retry-safe',
    label: 'Transient failure',
    nextAction: 'Retry the task — the failure is unrelated to conflicts or base movement.',
  },
  'branch-invariant-violation': {
    failureKind: 'branch-invariant-violation',
    label: 'Runner / action branch-invariant violation',
    nextAction: 'This is a runner or action bug: the workflow workspace left its expected run branch. Retry the task — the runner will restore the run branch automatically — and report the issue if it recurs. Issue work is not the cause.',
  },
}

export function isDeliveryFailureKind(value: unknown): value is DeliveryFailureKind {
  return (
    value === 'conflict' ||
    value === 'base-moved' ||
    value === 'retry-safe' ||
    value === 'branch-invariant-violation'
  )
}

export function getDeliveryFailureGuidance(kind: DeliveryFailureKind): DeliveryFailureGuidance {
  return DELIVERY_FAILURE_GUIDANCE[kind]
}

export interface DeliveryFailureResolution {
  failureKind: DeliveryFailureKind | null
  guidance: DeliveryFailureGuidance | null
  evidence: BranchInvariantEvidence | null
}

const KIND_IN_MESSAGE = /\((conflict|base-moved|retry-safe|branch-invariant-violation)\)/i
const BRANCH_INVARIANT_IN_MESSAGE = /\bbranch-invariant\s+violation\b(?:\s+at\s+(start|end)\s+boundary)?/i
const BRANCH_EVIDENCE_IN_MESSAGE =
  /expected\s+branch\s+'(?<expected>[^']*)'.*?observed\s+(?:'(?<observed>[^']*)'|detached\s+at\s+(?<ref>\S+))/i

export function resolveDeliveryFailureFromOutput(
  output: unknown,
): DeliveryFailureResolution {
  if (output == null) return { failureKind: null, guidance: null, evidence: null }
  const candidate = extractFailureKindCandidate(output)
  if (!candidate) return { failureKind: null, guidance: null, evidence: null }
  return resolveDeliveryFailure(candidate, output)
}

export function resolveDeliveryFailureFromMessage(
  message: string | null | undefined,
): DeliveryFailureResolution {
  if (!message) return { failureKind: null, guidance: null, evidence: null }
  const match = message.match(KIND_IN_MESSAGE)
  if (match) {
    return resolveDeliveryFailure(match[1], message)
  }
  if (BRANCH_INVARIANT_IN_MESSAGE.test(message)) {
    return resolveDeliveryFailure('branch-invariant-violation', message)
  }
  return { failureKind: null, guidance: null, evidence: null }
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
    const direct = record.failureKind ?? record.FailureKind
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
    return { failureKind: null, guidance: null, evidence: null }
  }
  const evidence =
    candidate === 'branch-invariant-violation'
      ? extractBranchInvariantEvidence(evidenceSource)
      : null
  return {
    failureKind: candidate,
    guidance: DELIVERY_FAILURE_GUIDANCE[candidate],
    evidence,
  }
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
