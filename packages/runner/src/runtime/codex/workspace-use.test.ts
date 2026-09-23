import { describe, expect, it } from 'vitest'
import { CodexWorkspaceUse } from './workspace-use.js'

describe('CodexWorkspaceUse', () => {
  it('retains a submitted turn until its exact terminal event', () => {
    const use = new CodexWorkspaceUse()
    const operation = use.begin(1, 'thread-1', '/work/REPOS/source')
    use.bindTurnId(operation, 'turn-1')

    expect(use.inspect('/work')).toBe('busy')
    use.completed(1, { type: 'turn/completed', threadId: 'thread-1', turnId: 'other', status: 'completed' })
    expect(use.inspect('/work')).toBe('busy')
    use.completed(1, { type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    expect(use.inspect('/work')).toBe('ready')
  })

  it('treats an unresolved directory handle as unknown resource ownership', () => {
    const use = new CodexWorkspaceUse()
    use.begin(1, 'thread-1', '/proc/999999/fd/7')

    expect(use.inspect('/work')).toBe('failed')
  })

  it('reconciles an exact completion received before the turn/start response binds', () => {
    const use = new CodexWorkspaceUse()
    use.completed(1, { type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    const operation = use.begin(1, 'thread-1', '/work')
    use.completed(1, { type: 'turn/completed', threadId: 'other-thread', turnId: 'turn-1', status: 'completed' })
    use.completed(1, { type: 'turn/completed', threadId: 'thread-1', turnId: 'other-turn', status: 'completed' })
    use.completed(2, { type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    use.bindTurnId(operation, 'turn-1')
    expect(use.inspect('/work')).toBe('busy')

    const next = use.begin(1, 'thread-1', '/work')
    use.completed(1, { type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-2', status: 'completed' })
    use.bindTurnId(next, 'turn-2')
    expect(use.inspect('/work')).toBe('busy')
    use.completed(1, { type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    expect(use.inspect('/work')).toBe('ready')
  })
})
