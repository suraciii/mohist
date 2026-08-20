import { createHash } from 'node:crypto'

import type { CommandConfig, ResourceLaneConfig, SuiteConfig, TrackConfig } from './types.js'

const ID_PATTERN = /^[a-z][a-z0-9-]*$/

export interface PlanSelection {
  readonly scope: 'application' | 'repository'
  readonly application?: string
  readonly tracks: readonly TrackConfig[]
}

export function planIdentity(config: SuiteConfig): string {
  return createHash('sha256').update(JSON.stringify(config.plan)).digest('hex')
}

export function validatePlan(config: SuiteConfig): string[] {
  const errors: string[] = []
  const plan = config.plan
  if (plan === undefined) return ['plan is required']

  if (!Array.isArray(plan.applications) || plan.applications.length === 0) {
    errors.push('plan.applications must be a non-empty array')
  }
  const applications = new Set<string>()
  for (const application of plan.applications ?? []) {
    if (typeof application !== 'string' || !ID_PATTERN.test(application)) {
      errors.push(`plan.applications contains invalid application id: ${String(application)}`)
      continue
    }
    if (applications.has(application))
      errors.push(`plan.applications contains duplicate application id: ${application}`)
    applications.add(application)
  }

  if (typeof plan.repositoryScope !== 'string' || !ID_PATTERN.test(plan.repositoryScope)) {
    errors.push('plan.repositoryScope must be a valid scope id')
  } else if (applications.has(plan.repositoryScope)) {
    errors.push(`plan.repositoryScope must not be an application id: ${plan.repositoryScope}`)
  }

  errors.push(...validateResourceLanes(plan.resourceLanes))
  errors.push(...validateApplicationBuilds(plan.applications, plan.applicationBuilds))
  errors.push(...validateCommandList(`plan.repositoryChecks`, plan.repositoryChecks))
  if (plan.fastChecks !== undefined) errors.push(...validateCommandList(`plan.fastChecks`, plan.fastChecks))

  const behaviorByApplication = new Map<string, number>()
  const trackIds = new Set<string>()
  for (const track of config.tracks) {
    if (trackIds.has(track.id)) errors.push(`plan contains duplicate track id: ${track.id}`)
    trackIds.add(track.id)
    errors.push(...validateTrackMetadata(track, applications, plan.repositoryScope))

    if (track.trackType === 'behavior' && track.application !== undefined) {
      behaviorByApplication.set(track.application, (behaviorByApplication.get(track.application) ?? 0) + 1)
    }

    if (!Array.isArray(track.resources)) {
      errors.push(`track "${track.id}": resources must be an array`)
      continue
    }
    const matchingLanes = plan.resourceLanes.filter(
      (lane) => resourceKey(lane.resources) === resourceKey(track.resources ?? []),
    )
    if (matchingLanes.length === 0) {
      errors.push(`track "${track.id}": resources do not map to a plan lane`)
    } else if (matchingLanes.length > 1) {
      errors.push(`track "${track.id}": resources map to multiple plan lanes`)
    }
  }

  for (const application of applications) {
    if ((behaviorByApplication.get(application) ?? 0) === 0) {
      errors.push(`plan application has no behavior track: ${application}`)
    }
  }
  return errors
}

export function selectApplicationTracks(config: SuiteConfig, application: string): PlanSelection {
  assertValidPlan(config)
  if (!config.plan!.applications.includes(application)) {
    throw new Error(`unknown application: ${application}`)
  }
  return {
    scope: 'application',
    application,
    tracks: config.tracks.filter(
      (track) =>
        (track.trackType === 'behavior' && track.application === application) ||
        (track.trackType === 'architecture' && track.architectureScope === application),
    ),
  }
}

export function selectRepositoryTracks(config: SuiteConfig): PlanSelection {
  assertValidPlan(config)
  const repositoryScope = config.plan!.repositoryScope
  return {
    scope: 'repository',
    tracks: config.tracks.filter(
      (track) => track.trackType === 'architecture' && track.architectureScope === repositoryScope,
    ),
  }
}

export function selectFastTracks(config: SuiteConfig): readonly TrackConfig[] {
  assertValidPlan(config)
  return config.tracks.filter(
    (track) => track.trackType === 'architecture' || (track.trackType === 'behavior' && track.level === 'L0'),
  )
}

export function selectPortfolioTracks(config: SuiteConfig): readonly TrackConfig[] {
  assertValidPlan(config)
  return config.tracks
}

export function formatApplicationHelp(config: SuiteConfig): string {
  assertValidPlan(config)
  const lines = ['Applications:']
  for (const application of config.plan!.applications) {
    const count = config.tracks.filter(
      (track) => track.trackType === 'behavior' && track.application === application,
    ).length
    lines.push(`  ${application} (${count} behavior tracks)`)
  }
  return lines.join('\n')
}

export function applicationBuilds(config: SuiteConfig, application: string): readonly CommandConfig[] {
  assertValidPlan(config)
  if (!config.plan!.applications.includes(application)) {
    throw new Error(`unknown application: ${application}`)
  }
  return config.plan!.applicationBuilds[application] ?? []
}

function assertValidPlan(config: SuiteConfig): void {
  const errors = validatePlan(config)
  if (errors.length > 0) throw new Error(`invalid test plan:\n${errors.map((error) => `  - ${error}`).join('\n')}`)
}

function validateTrackMetadata(
  track: TrackConfig,
  applications: ReadonlySet<string>,
  repositoryScope: string,
): string[] {
  const errors: string[] = []
  const prefix = `track "${track.id}"`
  if (track.trackType !== 'behavior' && track.trackType !== 'architecture') {
    errors.push(`${prefix}: trackType must be behavior or architecture`)
    return errors
  }
  if (track.specKind !== 'Product' && track.specKind !== 'Design') {
    errors.push(`${prefix}: specKind must be Product or Design`)
  }
  if (track.trackType === 'behavior') {
    if (track.application === undefined || !applications.has(track.application)) {
      errors.push(`${prefix}: behavior track must name a plan application`)
    }
    if (track.level !== 'L0' && track.level !== 'L1') {
      errors.push(`${prefix}: behavior track must declare Level L0 or L1`)
    }
    if (track.architectureScope !== undefined) {
      errors.push(`${prefix}: behavior track must not declare architectureScope`)
    }
  } else {
    if (track.specKind !== 'Design') errors.push(`${prefix}: Architecture track must use specKind=Design`)
    if (track.level !== undefined) errors.push(`${prefix}: Architecture track must not declare Level`)
    if (track.architectureScope === undefined) {
      errors.push(`${prefix}: Architecture track must declare architectureScope`)
    } else if (track.architectureScope !== repositoryScope && !applications.has(track.architectureScope)) {
      errors.push(`${prefix}: unknown architectureScope: ${track.architectureScope}`)
    }
    if (track.application !== undefined && track.application !== track.architectureScope) {
      errors.push(`${prefix}: application must match architectureScope when supplied`)
    }
  }
  return errors
}

function validateResourceLanes(lanes: readonly ResourceLaneConfig[] | undefined): string[] {
  const errors: string[] = []
  if (!Array.isArray(lanes) || lanes.length === 0) return ['plan.resourceLanes must be a non-empty array']
  const ids = new Set<string>()
  const keys = new Set<string>()
  for (const lane of lanes) {
    if (typeof lane.id !== 'string' || !ID_PATTERN.test(lane.id)) {
      errors.push(`plan.resourceLanes contains invalid lane id: ${String(lane.id)}`)
    } else if (ids.has(lane.id)) {
      errors.push(`plan.resourceLanes contains duplicate lane id: ${lane.id}`)
    } else {
      ids.add(lane.id)
    }
    if (
      !Array.isArray(lane.resources) ||
      lane.resources.some((resource) => typeof resource !== 'string' || !resource)
    ) {
      errors.push(`plan.resourceLane "${lane.id}": resources must contain non-empty strings`)
    } else {
      const key = resourceKey(lane.resources)
      if (keys.has(key)) errors.push(`plan.resourceLanes contains duplicate resource set: ${key || '(empty)'}`)
      keys.add(key)
    }
    if (!Number.isInteger(lane.capacity) || lane.capacity <= 0) {
      errors.push(`plan.resourceLane "${lane.id}": capacity must be a positive integer`)
    }
  }
  return errors
}

function validateApplicationBuilds(
  applications: readonly string[] | undefined,
  builds: Readonly<Record<string, readonly CommandConfig[]>> | undefined,
): string[] {
  const errors: string[] = []
  if (builds === undefined || builds === null || typeof builds !== 'object' || Array.isArray(builds)) {
    return ['plan.applicationBuilds must be an object']
  }
  const applicationSet = new Set(applications ?? [])
  for (const application of Object.keys(builds)) {
    if (!applicationSet.has(application)) {
      errors.push(`plan.applicationBuilds contains unknown application: ${application}`)
    }
  }
  for (const application of applications ?? []) {
    const commands = builds[application]
    errors.push(...validateCommandList(`plan.applicationBuilds.${application}`, commands))
  }
  return errors
}

function validateCommandList(prefix: string, commands: readonly CommandConfig[] | undefined): string[] {
  const errors: string[] = []
  if (!Array.isArray(commands) || commands.length === 0) {
    errors.push(`${prefix} must be a non-empty command list`)
    return errors
  }
  commands.forEach((command, index) => {
    if (command === null || typeof command !== 'object' || Array.isArray(command)) {
      errors.push(`${prefix}[${index}] must be a command object`)
      return
    }
    if (typeof command.command !== 'string' || command.command.length === 0) {
      errors.push(`${prefix}[${index}].command must be non-empty`)
    }
    if (!Array.isArray(command.args) || command.args.some((arg) => typeof arg !== 'string')) {
      errors.push(`${prefix}[${index}].args must be an array of strings`)
    }
  })
  return errors
}

function resourceKey(resources: readonly string[]): string {
  return [...resources].sort().join('\0')
}
