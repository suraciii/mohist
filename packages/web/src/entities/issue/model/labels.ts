export type LabelMap = Record<string, string>

export const LABEL_KEY_PATTERN = /^[a-z0-9]([-a-z0-9]*[a-z0-9])?$/

export interface LabelValidationError {
  key: string
  message: string
}

export function validateLabelKey(key: string): LabelValidationError | null {
  const trimmed = key
  if (trimmed.length === 0) {
    return { key: trimmed, message: 'Label key is required' }
  }
  if (trimmed !== trimmed.trim()) {
    return { key: trimmed, message: 'Label key cannot have leading or trailing whitespace' }
  }
  if (trimmed !== trimmed.toLowerCase()) {
    return { key: trimmed, message: 'Label key must be lowercase (letters and digits only)' }
  }
  if (!LABEL_KEY_PATTERN.test(trimmed)) {
    return {
      key: trimmed,
      message:
        'Label key must start/end with a letter or digit and contain only lowercase letters, digits, and dashes',
    }
  }
  return null
}

export function validateLabelValue(value: string): LabelValidationError | null {
  if (value.length === 0) {
    return { key: '', message: 'Label value is required' }
  }
  if (value.trim().length === 0) {
    return { key: '', message: 'Label value cannot be whitespace only' }
  }
  return null
}

export interface LabelEntryInput {
  key: string
  value: string
}

export interface ValidatedLabelEntry {
  key: string
  value: string
}

export function validateLabelEntry(entry: LabelEntryInput): { ok: true; entry: ValidatedLabelEntry } | { ok: false; error: string } {
  const keyError = validateLabelKey(entry.key)
  if (keyError) return { ok: false, error: keyError.message }
  const valueError = validateLabelValue(entry.value)
  if (valueError) return { ok: false, error: valueError.message }
  return { ok: true, entry: { key: entry.key.trim(), value: entry.value.trim() } }
}

export function parseLabelToken(token: string): { key: string; value: string } | null {
  const idx = token.indexOf('=')
  if (idx <= 0) return null
  const key = token.slice(0, idx)
  const value = token.slice(idx + 1)
  if (key.length === 0) return null
  return { key, value }
}

export function formatLabelToken(key: string, value: string): string {
  return `${key}=${value}`
}

export function serializeLabelTokens(entries: ReadonlyArray<{ key: string; value: string }>): string {
  return entries.map((e) => formatLabelToken(e.key, e.value)).join(',')
}

export function parseLabelTokensCsv(csv: string | null | undefined): Array<{ key: string; value: string }> {
  if (!csv) return []
  return csv
    .split(',')
    .map((t) => t.trim())
    .filter((t) => t.length > 0)
    .map((t) => parseLabelToken(t))
    .filter((parsed): parsed is { key: string; value: string } => parsed !== null)
}

export function serializeLabelSearchParams(params: URLSearchParams, labels: ReadonlyArray<string>): void {
  if (labels.length > 0) params.set('labelMode', 'repeated')
  for (const label of labels) {
    if (label.length > 0) params.append('labels', label)
  }
}

export function parseLabelSearchParams(params: URLSearchParams, rawSearch?: string): string[] {
  const repeated = params.getAll('labels').filter(Boolean)
  if (repeated.length > 1) return repeated
  if (params.get('labelMode') === 'repeated') return repeated
  if (rawSearch && (rawSearch.match(/(?:^|[?&])labels=/g) ?? []).length > 1) return repeated
  const single = repeated[0]
  return single ? single.split(',').filter(Boolean) : []
}

export function labelsToTokens(labels: LabelMap): string[] {
  return Object.keys(labels).sort().map((k) => formatLabelToken(k, labels[k]))
}

export function tokensToLabelMap(tokens: ReadonlyArray<string>): LabelMap {
  const result: LabelMap = {}
  for (const token of tokens) {
    const parsed = parseLabelToken(token)
    if (!parsed) continue
    result[parsed.key] = parsed.value
  }
  return result
}

export function labelMapEquals(a: LabelMap, b: LabelMap): boolean {
  const aKeys = Object.keys(a)
  const bKeys = Object.keys(b)
  if (aKeys.length !== bKeys.length) return false
  for (const key of aKeys) {
    if (a[key] !== b[key]) return false
  }
  return true
}

export function normalizeLabelMap(input: unknown): LabelMap {
  if (!input || typeof input !== 'object' || Array.isArray(input)) return {}
  const result: LabelMap = {}
  for (const [rawKey, rawValue] of Object.entries(input as Record<string, unknown>)) {
    if (typeof rawValue !== 'string') continue
    if (rawValue.length === 0 || rawValue.trim().length === 0) continue
    const keyError = validateLabelKey(rawKey)
    if (keyError) continue
    result[rawKey] = rawValue
  }
  return result
}

export function deriveSelectableLabelPairs(labels: LabelMap): Array<{ key: string; value: string }> {
  const pairs: Array<{ key: string; value: string }> = []
  for (const [key, value] of Object.entries(labels)) {
    pairs.push({ key, value })
  }
  pairs.sort((a, b) => {
    if (a.key !== b.key) return a.key < b.key ? -1 : 1
    return a.value < b.value ? -1 : a.value > b.value ? 1 : 0
  })
  return pairs
}

export function deriveLabelKeysFromIssues(
  issues: ReadonlyArray<{ labels?: unknown }>,
): string[] {
  const set = new Set<string>()
  for (const issue of issues) {
    if (!issue.labels || typeof issue.labels !== 'object' || Array.isArray(issue.labels)) continue
    for (const key of Object.keys(issue.labels as Record<string, unknown>)) {
      set.add(key)
    }
  }
  return Array.from(set).sort()
}

export function deriveLabelPairsFromIssues(
  issues: ReadonlyArray<{ labels?: unknown }>,
): Array<{ key: string; value: string }> {
  const seen = new Map<string, string>()
  for (const issue of issues) {
    if (!issue.labels || typeof issue.labels !== 'object' || Array.isArray(issue.labels)) continue
    const normalized = normalizeLabelMap(issue.labels)
    for (const [key, value] of Object.entries(normalized)) {
      const composite = `${key}=${value}`
      if (!seen.has(composite)) seen.set(composite, value)
    }
  }
  const pairs: Array<{ key: string; value: string }> = []
  for (const [composite] of seen) {
    const parsed = parseLabelToken(composite)
    if (parsed) pairs.push(parsed)
  }
  pairs.sort((a, b) => {
    if (a.key !== b.key) return a.key < b.key ? -1 : 1
    return a.value < b.value ? -1 : a.value > b.value ? 1 : 0
  })
  return pairs
}
