import { describe, expect, it } from 'vitest'
import { CodexWorkspaceUse } from './workspace-use.js'

describe('CodexWorkspaceUse', () => {
  it('retains a submitted turn until its exact terminal event', () => {
    const use = new CodexWorkspaceUse()
    const operation = use.begin('thread-1', '/work/REPOS/source')
    operation.turnId = 'turn-1'

    expect(use.inspect('/work')).toBe('busy')
    use.completed({ type: 'turn/completed', threadId: 'thread-1', turnId: 'other', status: 'completed' })
    expect(use.inspect('/work')).toBe('busy')
    use.completed({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    expect(use.inspect('/work')).toBe('ready')
  })

  it('treats an unresolved directory handle as unknown resource ownership', () => {
    const use = new CodexWorkspaceUse()
    use.begin('thread-1', '/proc/999999/fd/7')

    expect(use.inspect('/work')).toBe('failed')
  })
})
