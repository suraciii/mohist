import assert from 'node:assert/strict'
import { test } from 'node:test'

import { formatApplicationHelp, selectApplicationTracks, selectRepositoryTracks, validatePlan } from './plan.js'
import type { SuiteConfig, TrackConfig } from './types.js'

function track(overrides: Partial<TrackConfig> = {}): TrackConfig {
  return {
    id: 'server-unit',
    kind: 'report-only',
    trackType: 'behavior',
    application: 'server',
    specKind: 'Design',
    level: 'L0',
    resources: [],
    report: 'reports/server-unit.json',
    reportFormat: 'vitest',
    deadlineMs: 1000,
    enforce: true,
    ...overrides,
  }
}

function planConfig(
  tracks: readonly TrackConfig[] = [
    track(),
    track({ id: 'server-arch', trackType: 'architecture', level: undefined, architectureScope: 'server' }),
  ],
): SuiteConfig {
  return {
    suiteDeadlineMs: 300_000,
    plan: {
      applications: ['server'],
      repositoryScope: 'repository',
      applicationBuilds: { server: [{ command: 'dotnet', args: ['build'] }] },
      resourceLanes: [{ id: 'default', resources: [], capacity: 1 }],
    },
    tracks,
  }
}

test('validatePlan accepts behavior and application Architecture tracks', () => {
  assert.deepEqual(validatePlan(planConfig()), [])
})

test('selectApplicationTracks returns only the complete application behavior scope', () => {
  const selection = selectApplicationTracks(planConfig(), 'server')
  assert.equal(selection.scope, 'application')
  assert.deepEqual(
    selection.tracks.map((item) => item.id),
    ['server-unit', 'server-arch'],
  )
})

test('selectRepositoryTracks returns only repository Architecture tracks', () => {
  const config = planConfig([
    track(),
    track({
      id: 'repository-arch',
      trackType: 'architecture',
      level: undefined,
      architectureScope: 'repository',
      application: undefined,
    }),
  ])
  assert.deepEqual(
    selectRepositoryTracks(config).tracks.map((item) => item.id),
    ['repository-arch'],
  )
})

test('selectApplicationTracks rejects an unknown application before execution', () => {
  assert.throws(() => selectApplicationTracks(planConfig(), 'web'), /unknown application: web/)
})

test('validatePlan rejects a behavior track without a Level', () => {
  const errors = validatePlan(planConfig([track({ level: undefined })]))
  assert.ok(errors.some((error) => error.includes('behavior track must declare Level L0 or L1')))
})

test('validatePlan rejects duplicate Resource lanes for one Resource set', () => {
  const config = planConfig()
  const errors = validatePlan({
    ...config,
    plan: {
      ...config.plan!,
      resourceLanes: [
        { id: 'default', resources: [], capacity: 1 },
        { id: 'also-default', resources: [], capacity: 1 },
      ],
    },
  })
  assert.ok(errors.some((error) => error.includes('duplicate resource set')))
  assert.ok(errors.some((error) => error.includes('resources map to multiple plan lanes')))
})

test('formatApplicationHelp derives applications from the plan', () => {
  assert.equal(formatApplicationHelp(planConfig()), 'Applications:\n  server (1 behavior tracks)')
})
