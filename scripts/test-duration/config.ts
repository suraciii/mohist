import type { AllowlistEntry, BudgetRule, CanonicalGateConfig, SuiteConfig, TrackConfig } from './types.js'

export function stripJsonc(text: string): string {
  let out = ''
  let i = 0
  let inString = false
  while (i < text.length) {
    const ch = text[i]
    const next = text[i + 1]
    if (inString) {
      out += ch
      if (ch === '\\' && i + 1 < text.length) {
        out += next
        i += 2
        continue
      }
      if (ch === '"') inString = false
      i += 1
      continue
    }
    if (ch === '"') {
      inString = true
      out += ch
      i += 1
      continue
    }
    if (ch === '/' && next === '/') {
      while (i < text.length && text[i] !== '\n') i += 1
      continue
    }
    if (ch === '/' && next === '*') {
      i += 2
      while (i < text.length && !(text[i] === '*' && text[i + 1] === '/')) i += 1
      i += 2
      continue
    }
    out += ch
    i += 1
  }
  return out
}

export function parseSuiteConfig(text: string): SuiteConfig {
  const raw = JSON.parse(stripJsonc(text)) as SuiteConfig
  return raw
}

export function validateConfig(config: SuiteConfig): string[] {
  const errors: string[] = []
  if (!Number.isFinite(config.suiteDeadlineMs) || config.suiteDeadlineMs <= 0) {
    errors.push('suiteDeadlineMs must be a positive number of milliseconds')
  }
  if (!Array.isArray(config.tracks) || config.tracks.length === 0) {
    errors.push('tracks must be a non-empty array')
    return errors
  }
  const ids = new Set<string>()
  for (const track of config.tracks) {
    if (ids.has(track.id)) errors.push(`duplicate track id: ${track.id}`)
    ids.add(track.id)
    errors.push(...validateTrack(track))
  }
  if (config.canonical !== undefined) errors.push(...validateCanonical(config.canonical, config.tracks))
  return errors
}

function validateTrack(track: TrackConfig): string[] {
  const errors: string[] = []
  const prefix = `track "${track.id}"`
  if (track.executionLedger !== undefined && track.kind !== 'dotnet-apphost') {
    errors.push(`${prefix}: executionLedger requires kind=dotnet-apphost`)
  }
  if (track.executionLedger !== undefined && track.reportFormat !== 'trx') {
    errors.push(`${prefix}: executionLedger requires reportFormat=trx`)
  }
  if (track.executionLedger !== undefined && track.executionLedger.length === 0) {
    errors.push(`${prefix}: executionLedger must be a non-empty path`)
  }
  if (track.executionLedger !== undefined && !track.executionProvenance) {
    errors.push(`${prefix}: executionLedger requires executionProvenance`)
  }
  if (track.executionLedger === undefined && track.executionProvenance !== undefined) {
    errors.push(`${prefix}: executionProvenance requires executionLedger`)
  }
  if (track.executionProvenance !== undefined && track.executionProvenance.length === 0) {
    errors.push(`${prefix}: executionProvenance must be a non-empty path`)
  }
  if (track.executionLedger !== undefined && (!track.executionSourceRoots || track.executionSourceRoots.length === 0)) {
    errors.push(`${prefix}: executionLedger requires non-empty executionSourceRoots`)
  }
  if (track.executionSourceRoots?.some((root) => typeof root !== 'string' || root.length === 0)) {
    errors.push(`${prefix}: executionSourceRoots must contain only non-empty paths`)
  }
  if (track.apphostArgs !== undefined && !Array.isArray(track.apphostArgs)) {
    errors.push(`${prefix}: apphostArgs must be an array of strings`)
  } else if (track.apphostArgs?.some((arg) => typeof arg !== 'string')) {
    errors.push(`${prefix}: apphostArgs must contain only strings`)
  }
  if (track.deadlineMs <= 0) errors.push(`${prefix}: deadlineMs must be positive`)
  if (track.partitions !== undefined && (!Number.isInteger(track.partitions) || track.partitions < 2)) {
    errors.push(`${prefix}: partitions must be an integer greater than one`)
  }
  if (track.partitions !== undefined && track.kind !== 'dotnet-apphost') {
    errors.push(`${prefix}: partitions require kind dotnet-apphost`)
  }
  if (track.partitions !== undefined && !track.report.includes('{partition}')) {
    errors.push(`${prefix}: partitioned reports must include {partition}`)
  }
  if (track.partitions !== undefined && track.executionLedger !== undefined) {
    errors.push(`${prefix}: executionLedger tracks cannot be partitioned`)
  }
  if (track.kind !== 'report-only' && !track.run && !track.csproj && !track.apphost) {
    errors.push(`${prefix}: needs a run command, csproj, or apphost`)
  }
  if (track.reportFormat !== 'trx' && track.reportFormat !== 'vitest') {
    errors.push(`${prefix}: unknown reportFormat "${track.reportFormat}"`)
  }
  if (track.enforce) {
    if (track.status === 'baseline-pending') {
      errors.push(`${prefix}: enforce=true cannot use status baseline-pending`)
    }
    const rules = track.rules ?? []
    if (rules.length === 0) {
      errors.push(`${prefix}: enforce=true requires at least one rule`)
    }
    if (rules.length > 0) {
      const last = rules[rules.length - 1]
      if (last.namePattern !== undefined) {
        errors.push(`${prefix}: last rule "${last.id}" must omit namePattern to act as the default catch-all`)
      }
    }
    for (const rule of rules) errors.push(...validateRule(rule, prefix))
  } else {
    if (track.status !== 'baseline-pending') {
      errors.push(`${prefix}: enforce=false requires status baseline-pending`)
    }
    if (!track.reason?.trim()) {
      errors.push(`${prefix}: enforce=false requires a non-empty baseline-pending reason`)
    }
    if ((track.rules?.length ?? 0) > 0) {
      errors.push(`${prefix}: enforce=false must not carry unenforced rules`)
    }
  }
  return errors
}

function validateCanonical(config: CanonicalGateConfig, tracks: readonly TrackConfig[]): string[] {
  const errors: string[] = []
  if (!Number.isInteger(config.maxConcurrentLanes) || config.maxConcurrentLanes <= 0) {
    errors.push('canonical.maxConcurrentLanes must be a positive integer')
  }
  if (config.resourceLimits === null || typeof config.resourceLimits !== 'object' || Array.isArray(config.resourceLimits)) {
    errors.push('canonical.resourceLimits must be an object')
    return errors
  }
  for (const [resource, limit] of Object.entries(config.resourceLimits)) {
    if (!Number.isInteger(limit) || limit <= 0) {
      errors.push(`canonical.resourceLimits.${resource} must be a positive integer`)
    }
  }
  if (config.durationMeasurementTracks !== undefined) {
    if (!Array.isArray(config.durationMeasurementTracks) || config.durationMeasurementTracks.length === 0) {
      errors.push('canonical.durationMeasurementTracks must be a non-empty array of track ids')
      return errors
    }
    if (config.resourceLimits['duration-measurement'] === undefined) {
      errors.push('canonical.durationMeasurementTracks requires canonical.resourceLimits.duration-measurement')
    }
    const knownTracks = new Map(tracks.map((track) => [track.id, track]))
    const seen = new Set<string>()
    for (const trackId of config.durationMeasurementTracks) {
      if (typeof trackId !== 'string' || !trackId) {
        errors.push('canonical.durationMeasurementTracks must contain only non-empty track ids')
        continue
      }
      if (seen.has(trackId)) {
        errors.push(`canonical.durationMeasurementTracks contains duplicate track id: ${trackId}`)
        continue
      }
      seen.add(trackId)
      const track = knownTracks.get(trackId)
      if (!track) {
        errors.push(`canonical.durationMeasurementTracks references unknown track: ${trackId}`)
      } else if (track.partitions !== undefined) {
        errors.push(`canonical.durationMeasurementTracks cannot include partitioned track: ${trackId}`)
      }
    }
  }
  if (config.durationIsolationTrack !== undefined) {
    if (config.durationMeasurementTracks === undefined) {
      errors.push('canonical.durationIsolationTrack requires canonical.durationMeasurementTracks')
    }
    if (typeof config.durationIsolationTrack !== 'string' || !config.durationIsolationTrack) {
      errors.push('canonical.durationIsolationTrack must be a non-empty track id')
    } else {
      const track = new Map(tracks.map((candidate) => [candidate.id, candidate])).get(config.durationIsolationTrack)
      if (!track) errors.push(`canonical.durationIsolationTrack references unknown track: ${config.durationIsolationTrack}`)
      else if (track.partitions !== undefined) errors.push(`canonical.durationIsolationTrack cannot include partitioned track: ${config.durationIsolationTrack}`)
      else if (track.kind !== 'vitest') errors.push(`canonical.durationIsolationTrack must reference a vitest track: ${config.durationIsolationTrack}`)
    }
  }
  return errors
}

function validateRule(rule: BudgetRule, prefix: string): string[] {
  const errors: string[] = []
  const rp = `${prefix}: rule "${rule.id}"`
  if (!rule.id) errors.push(`${rp}: missing id`)
  if (rule.absoluteMs <= 0) errors.push(`${rp}: absoluteMs must be positive`)
  if (
    rule.percentile !== undefined &&
    (rule.percentileMs === undefined || rule.percentileMs < 0)
  ) {
    errors.push(`${rp}: percentile set without a valid percentileMs`)
  }
  for (const entry of rule.allowlist ?? []) {
    errors.push(...validateEntry(entry, rp))
  }
  return errors
}

function validateEntry(entry: AllowlistEntry, prefix: string): string[] {
  const errors: string[] = []
  const key = entry.id ?? entry.pattern ?? ''
  if (!key) errors.push(`${prefix}: allowlist entry needs id or pattern`)
  if (entry.id !== undefined && entry.pattern !== undefined) {
    errors.push(`${prefix}: allowlist entry "${entry.id}" has both id and pattern`)
  }
  if (!entry.reason) errors.push(`${prefix}: allowlist entry "${key}" needs a reason`)
  if (!entry.owner) errors.push(`${prefix}: allowlist entry "${key}" needs an owner`)
  if (!Number.isFinite(entry.observedMs) || entry.observedMs <= 0) {
    errors.push(`${prefix}: allowlist entry "${key}" needs a positive observedMs`)
  }
  if (!entry.deadline || Number.isNaN(Date.parse(entry.deadline))) {
    errors.push(`${prefix}: allowlist entry "${key}" needs a valid ISO date deadline`)
  }
  return errors
}
