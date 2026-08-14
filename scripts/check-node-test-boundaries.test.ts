import assert from 'node:assert/strict'
import { test } from 'node:test'
import { resolve } from 'node:path'
import {
  scanRunnerSourceFile,
  scanSourceFile,
} from './check-node-test-boundaries.js'

function rules(violations: Array<{ rule: string }>): Set<string> {
  return new Set(violations.map((violation) => violation.rule))
}

test('Web test scanner rejects every guarded boundary', () => {
  const violations = scanSourceFile(
    '/virtual/packages/web/src/boundary.test.tsx',
    `
      // @vitest-environment jsdom
      window.location = 'https://example.test'
      globalThis.fetch = async () => new Response()
      vi.stubGlobal('fetch', () => undefined)
      vi.mock('./module')
      const mock = vi.mock
      it('issue-123 keeps old coverage', () => undefined)
      import { readFileSync } from 'node:fs'
      readFileSync('source.ts')
      expect(document.body.getBoundingClientRect().width).toBe(window.innerWidth)
    `,
  )

  assert.deepEqual(rules(violations), new Set([
    'no-direct-shared-state-mutation',
    'no-web-fetch-global-mutation',
    'no-web-fetch-global-stub',
    'no-web-vi-mock',
    'no-historical-ticket-test-title',
    'no-web-node-fs-source-read',
    'no-jsdom-page-geometry-assertion',
    'no-vitest-environment-directive',
  ]))
  assert.ok(violations.every((violation) => violation.line > 0 && violation.column > 0))
})

test('Web test scanner rejects DOM use from a plain Node test', () => {
  const violations = scanSourceFile(
    '/virtual/packages/web/src/boundary.test.ts',
    `
      import { render } from '@testing-library/react'
      render(document.body)
    `,
  )

  assert.ok(rules(violations).has('no-dom-in-plain-web-test'))
})

test('Runner test scanner rejects every guarded default-track boundary', () => {
  const runnerRoot = resolve('packages/runner')
  const violations = scanRunnerSourceFile(
    resolve(runnerRoot, 'tests/boundary.test.ts'),
    `
      import { spawn } from 'node:child_process'
      import { runCommand } from '../src/system/process.js'
      import '../scripts/write-build-info.ts'
      vi.mock('../src/system/process-policy.js')
      it.skip('disabled', () => undefined)
      const startedAt = Date.now()
      expect(Date.now() - startedAt).toBe(1)
      await new Promise((resolve) => setTimeout(resolve, 1))
      await runCommand('git', ['status'])
      void spawn
    `,
    { runnerRoot },
  )

  assert.deepEqual(rules(violations), new Set([
    'no-default-runner-child-process-import',
    'no-default-runner-executable-script-import',
    'no-default-runner-process-policy-mock',
    'no-runner-test-modifier',
    'no-elapsed-time-assertion',
    'no-real-time-sleep',
    'no-default-runner-platform-command',
  ]))
})

test('Runner test scanner rejects waitFor in RunnerHost specs and unsupported integration modifiers', () => {
  const runnerRoot = resolve('packages/runner')
  const hostViolations = scanRunnerSourceFile(
    resolve(runnerRoot, 'src/runtime/runner-host.spec.ts'),
    'await vi.waitFor(() => undefined)',
    { runnerRoot },
  )
  const integrationViolations = scanRunnerSourceFile(
    resolve(runnerRoot, 'tests/integration/boundary.spec.ts'),
    "it.skip('unsupported', () => undefined)",
    { runnerRoot, track: 'integration' },
  )

  assert.ok(rules(hostViolations).has('no-runner-host-wait-for'))
  assert.ok(rules(integrationViolations).has('no-runner-test-modifier'))
})
