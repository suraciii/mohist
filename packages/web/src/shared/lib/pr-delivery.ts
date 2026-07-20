export interface PrDeliveryMetadata {
  prNumber: number
  prUrl: string
  mergeCommitSha: string | null
  targetBranch: string | null
}

export function extractPrDeliveryMetadata(taskOutput: unknown): PrDeliveryMetadata | null {
  const record = readPublishViaPrRecord(taskOutput)
  if (!record) return null

  const prNumber = readNumber(record['prNumber'])
  const prUrl = readString(record['prUrl'])
  if (prNumber == null || !prUrl) return null

  return {
    prNumber,
    prUrl,
    mergeCommitSha: readString(record['mergeCommitSha']),
    targetBranch: readString(record['targetBranch']),
  }
}

function readPublishViaPrRecord(value: unknown): Record<string, unknown> | null {
  if (value == null || typeof value !== 'object' || Array.isArray(value)) return null
  const record = value as Record<string, unknown>
  if (
    record['kind'] !== 'publish-via-pr' &&
    record['kind'] !== 'create-pull-request' &&
    record['kind'] !== 'merge-pull-request'
  ) return null
  return record
}

function readNumber(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const parsed = Number(value)
    return Number.isFinite(parsed) ? parsed : null
  }
  return null
}

function readString(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  return trimmed.length > 0 ? trimmed : null
}
