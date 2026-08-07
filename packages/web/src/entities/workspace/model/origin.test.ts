import { describe, expect, it } from 'vitest'
import { workspaceOriginLabel } from './origin'
import type { WorkspaceOrigin } from './types'

describe('workspaceOriginLabel', () => {
  it.each<[WorkspaceOrigin, string]>([
    [{ kind: 'issue', issueNumber: 42 }, 'Issue #42'],
    [{ kind: 'issue' }, 'Issue #?'],
    [{ kind: 'slack', teamId: 'T1', channelId: 'C1' }, 'Slack'],
    [{ kind: 'web', conversationId: 'conv-1' }, 'Web'],
    [{ kind: 'manual' }, 'Manual'],
    [{ kind: 'unknown' }, 'Unknown'],
  ])('formats %o as %s', (origin, expected) => {
    expect(workspaceOriginLabel(origin)).toBe(expected)
  })
})
