import assert from 'node:assert/strict'
import { test } from 'node:test'

import { partitionExecutionArguments, planPartitionClasses, verifyPartitionArtifacts } from './spec-partition.js'

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
  assert.deepEqual(first.selectedClasses, ['Mohist.Server.SpecTests.AlphaSpecs', 'Mohist.Server.SpecTests.DeltaSpecs'])
  assert.deepEqual(second.selectedClasses, ['Mohist.Server.SpecTests.BetaSpecs', 'Mohist.Server.SpecTests.ZetaSpecs'])
})

test('Spec partition planning balances whole classes by discovered case count', () => {
  const classes = [
    'Mohist.Server.SpecTests.AlphaSpecs',
    'Mohist.Server.SpecTests.BetaSpecs',
    'Mohist.Server.SpecTests.CharlieSpecs',
    'Mohist.Server.SpecTests.DeltaSpecs',
    'Mohist.Server.SpecTests.EchoSpecs',
  ].join('\n')
  const tests = [
    ...Array.from({ length: 1 }, (_, index) => `Mohist.Server.SpecTests.AlphaSpecs.Case${index}`),
    ...Array.from({ length: 10 }, (_, index) => `Mohist.Server.SpecTests.BetaSpecs.Case${index}`),
    ...Array.from({ length: 20 }, (_, index) => `Mohist.Server.SpecTests.CharlieSpecs.Case${index}`),
    ...Array.from({ length: 20 }, (_, index) => `Mohist.Server.SpecTests.DeltaSpecs.Case${index}`),
    ...Array.from({ length: 20 }, (_, index) => `Mohist.Server.SpecTests.EchoSpecs.Case${index}`),
  ].join('\n')
  const partitions = [0, 1, 2].map((index) => planPartitionClasses(classes, index, 3, tests))

  assert.deepEqual(
    partitions.map((partition) => partition.selectedCaseCount),
    [30, 21, 20],
  )
  assert.deepEqual(new Set(partitions.flatMap((partition) => partition.selectedClasses)), new Set(classes.split('\n')))
  assert.equal(
    partitions.reduce((total, partition) => total + partition.selectedCaseCount, 0),
    71,
  )
})

test('Spec partition execution fixes the aggregate inner concurrency and keeps whole-class filters', () => {
  assert.deepEqual(partitionExecutionArguments('/evidence/spec.trx', ['Ns.Alpha', 'Ns.Beta'], 1), [
    '-noColor',
    '-noLogo',
    '-noAutoReporters',
    '-trx',
    '/evidence/spec.trx',
    '-parallel',
    'collections',
    '-parallelAlgorithm',
    'conservative',
    '-maxThreads',
    '1',
    '-class',
    'Ns.Alpha',
    '-class',
    'Ns.Beta',
  ])
  assert.throws(() => partitionExecutionArguments('/evidence/spec.trx', ['Ns.Alpha'], 0), /positive integer/)
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
    () =>
      verifyPartitionArtifacts([
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
