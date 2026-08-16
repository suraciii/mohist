import { describe, expect, it } from 'vitest'
import { processAction } from '../../src/actions/built-in-core.js'
import { probePrlimit, setPrlimitAvailabilityForTests } from '../../src/system/process.js'
import { makeHost } from '../support/action-host-test.js'
import { withTestRunnerResources } from '../support/test-resources.js'

describe('runner resource containment integration', () => {
  it('contains a memory-burning child without terminating the runner process', async () => {
    const available = await probePrlimit()
    setPrlimitAvailabilityForTests(available)
    try {
      const result = await withTestRunnerResources(
        async () =>
          await processAction(
            {
              command: process.execPath,
              args: ['-e', 'const chunks=[]; while(true) chunks.push(new Array(100000).fill(1))'],
            },
            makeHost({
              workDir: process.cwd(),
              resourceLimits: { memoryMb: 64, wallClockMs: 3_000, watchdogIntervalMs: 25 },
            }),
          ),
        {
          externalProcessPolicy: {
            assertAllowed() {},
            register() {},
          },
        },
      )

      expect(result).toMatchObject({ error: { code: 'resource-containment' } })
    } finally {
      setPrlimitAvailabilityForTests(undefined)
    }
  })

  it.runIf(process.platform === 'linux')(
    'contains a memory-burning child through production RSS sampling when prlimit is disabled',
    async () => {
      setPrlimitAvailabilityForTests(false)
      try {
        const result = await withTestRunnerResources(
          async () =>
            await processAction(
              {
                command: process.execPath,
                args: ['-e', 'const chunks=[]; while(true) chunks.push(new Array(100000).fill(1))'],
              },
              makeHost({
                workDir: process.cwd(),
                resourceLimits: { memoryMb: 64, wallClockMs: null, watchdogIntervalMs: 25 },
              }),
            ),
          {
            externalProcessPolicy: {
              assertAllowed() {},
              register() {},
            },
          },
        )

        expect(result).toMatchObject({ error: { code: 'resource-containment' } })
      } finally {
        setPrlimitAvailabilityForTests(undefined)
      }
    },
  )

  it('does not terminate a sibling command when the runaway command is contained', async () => {
    const available = await probePrlimit()
    setPrlimitAvailabilityForTests(available)
    try {
      const [runaway, sibling] = await withTestRunnerResources(
        async () =>
          await Promise.all([
            processAction(
              {
                command: process.execPath,
                args: ['-e', 'const chunks=[]; while(true) chunks.push(new Array(100000).fill(1))'],
              },
              makeHost({
                workDir: process.cwd(),
                resourceLimits: { memoryMb: 64, wallClockMs: 3_000, watchdogIntervalMs: 25 },
              }),
            ),
            processAction(
              { command: 'printf', args: ['sibling'] },
              makeHost({
                workDir: process.cwd(),
                resourceLimits: { memoryMb: 64, wallClockMs: 3_000, watchdogIntervalMs: 25 },
              }),
            ),
          ]),
        {
          externalProcessPolicy: {
            assertAllowed() {},
            register() {},
          },
        },
      )

      expect(runaway).toMatchObject({ error: { code: 'resource-containment' } })
      expect(sibling).toMatchObject({ output: { stdout: 'sibling', exitCode: 0 } })
    } finally {
      setPrlimitAvailabilityForTests(undefined)
    }
  })
})
