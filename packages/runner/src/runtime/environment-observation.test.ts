import { createHash } from 'node:crypto'
import { describe, expect, it } from 'vitest'
import { observeRunnerEnvironment } from './environment-observation.js'

describe('observeRunnerEnvironment', () => {
  it('filters the allowlist and produces the Go-compatible digest', () => {
    const environment = {
      PATH: '/opt/go/bin:/tmp/injected:/opt/go/bin:relative:/opt/node/bin',
      TMPDIR: '/tmp/session',
      GOROOT: '/opt/go',
      GOPATH: '/opt/go-work',
      MOHIST_TOKEN: 'must-not-be-read',
    }
    const observed = observeRunnerEnvironment(environment, '2026-09-18T00:00:00.000Z', 'linux')
    const canonical = 'GOPATH=/opt/go-work\nGOROOT=/opt/go\nPATH=/opt/go/bin:/opt/node/bin\n'

    expect(observed.loadedAt).toBe('2026-09-18T00:00:00.000Z')
    expect(observed.version).toBe(createHash('sha256').update(canonical).digest('hex'))
  })

  it('fails closed when a loaded tool path is invalid', () => {
    expect(observeRunnerEnvironment({ GOROOT: 'relative/path' }, 'loaded', 'linux')).toEqual({
      version: null,
      loadedAt: 'loaded',
    })
  })

  it('does not report a service environment version on non-Linux hosts', () => {
    expect(observeRunnerEnvironment({ PATH: '/opt/go/bin' }, 'loaded', 'darwin')).toEqual({
      version: null,
      loadedAt: 'loaded',
    })
  })
})
