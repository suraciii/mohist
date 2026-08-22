import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import type { IngressResponseOwner, IngressResult } from './types.js'

const fixturePath = resolve(process.cwd(), '../../fixtures/slack-ingress-response-ownership.json')

describe('shared Slack ingress response ownership fixtures', () => {
  it('uses the representative server, adapter, and none outcomes', () => {
    const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as Record<string, IngressResult>
    const owners = Object.values(fixture).map((result) => result.responseOwner)
    expect(owners).toEqual(expect.arrayContaining<IngressResponseOwner>(['server', 'adapter', 'none']))
    expect(fixture.server?.responseOwner).toBe('server')
    expect(fixture.adapter?.responseOwner).toBe('adapter')
    expect(fixture.none?.responseOwner).toBe('none')
  })
})
