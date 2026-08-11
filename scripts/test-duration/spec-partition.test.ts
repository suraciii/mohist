import assert from 'node:assert/strict'
import { test } from 'node:test'

import { planPartitionClasses, verifyPartitionArtifacts } from './spec-partition.js'

const discovered = [
  'Mohist.Server.SpecTests.ZetaSpecs',
  'Mohist.Server.SpecTests.AlphaSpecs',
  'Mohist.Server.SpecTests.BetaSpecs',
  'Mohist.Server.SpecTests.DeltaSpecs',
].join('\n')

test('Spec partition planning sorts discovery and assigns a deterministic disjoint class subset', () => {
  const first = planPartitionClasses(discovered, 0, 2)
  const second = planPartitionClasses(discovered, 1, 2)

  assert.deepEqual(first.allClasses, [
    'Mohist.Server.SpecTests.AlphaSpecs',
    'Mohist.Server.SpecTests.BetaSpecs',
    'Mohist.Server.SpecTests.DeltaSpecs',
    'Mohist.Server.SpecTests.ZetaSpecs',
  ])
  assert.deepEqual(first.selectedClasses, [
    'Mohist.Server.SpecTests.AlphaSpecs',
    'Mohist.Server.SpecTests.DeltaSpecs',
  ])
  assert.deepEqual(second.selectedClasses, [
    'Mohist.Server.SpecTests.BetaSpecs',
    'Mohist.Server.SpecTests.ZetaSpecs',
  ])
})

test('Spec coverage verification rejects duplicate selection and requires every partition', () => {
  const first = planPartitionClasses(discovered, 0, 2)
  const second = planPartitionClasses(discovered, 1, 2)
  assert.deepEqual(
    verifyPartitionArtifacts([
      { directory: 'partition-0', ...first },
      { directory: 'partition-1', ...second },
    ]),
    { classes: 4, partitions: 2 },
  )
  assert.throws(
    () => verifyPartitionArtifacts([
      { directory: 'partition-0', ...first },
      { directory: 'partition-1', ...first, index: 1 },
    ]),
    /classes selected more than once/,
  )
  assert.throws(
    () => verifyPartitionArtifacts([{ directory: 'partition-0', ...first }]),
    /expected 2 partition artifacts, found 1/,
  )
})
