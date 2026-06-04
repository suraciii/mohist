import { describe, it, expect, vi } from 'vitest'
import { deriveToolCallTitle, reconstructRoundsFromEvents } from './useSessionTimeline'

describe('deriveToolCallTitle', () => {
  it('returns title when title differs from toolName', () => {
    expect(deriveToolCallTitle('read', 'server.ts', '{}')).toBe('server.ts')
  })

  it('derives filename from JSON file_path for read tool', () => {
    expect(
      deriveToolCallTitle('read', 'read', '{"file_path":"packages/server/src/Mohist.Server/Program.cs"}')
    ).toBe('Program.cs')
  })

  it('derives command from JSON command for bash tool', () => {
    expect(
      deriveToolCallTitle('bash', 'bash', '{"command":"npm run build"}')
    ).toBe('npm run build')
  })

  it('returns rawInput string when JSON parse fails', () => {
    expect(deriveToolCallTitle('bash', 'bash', 'npm test')).toBe('npm test')
  })

  it('returns toolName when rawInput is null', () => {
    expect(deriveToolCallTitle('unknown', 'unknown', null as unknown as string)).toBe('unknown')
  })

  it('returns toolName when rawInput is undefined', () => {
    expect(deriveToolCallTitle('read', 'read', undefined)).toBe('read')
  })

  it('truncates long bash commands', () => {
    const longCmd = 'a'.repeat(100)
    expect(deriveToolCallTitle('bash', 'bash', `{"command":"${longCmd}"}`)).toBe(
      'a'.repeat(57) + '...'
    )
  })

  it('derives pattern from glob tool', () => {
    expect(deriveToolCallTitle('glob', 'glob', '{"pattern":"**/*.ts"}')).toBe('**/*.ts')
  })

  it('handles filePath variant for read tool', () => {
    expect(
      deriveToolCallTitle('read_file', 'read_file', '{"filePath":"src/main.ts"}')
    ).toBe('main.ts')
  })
})

function makeSessionEvent(overrides: Partial<{
  id: number
  sequence: number
  type: string
  payload: unknown
  createdAt: string
}> = {}) {
  const sequence = overrides.sequence ?? 0
  return {
    id: overrides.id ?? sequence,
    sequence,
    type: overrides.type ?? 'mohist_prompt',
    payload: overrides.payload,
    createdAt: overrides.createdAt ?? '2024-01-01T00:00:00.000Z',
  }
}

describe('reconstructRoundsFromEvents', () => {
  it('returns empty array for empty events', () => {
    expect(reconstructRoundsFromEvents([])).toEqual([])
  })

  it('routes events through viewSessionEvents timeline projection', async () => {
    const viewModule = await import('../../../entities/session/model/view')
    const spy = vi.spyOn(viewModule, 'viewSessionEvents')
    try {
      const events = [makeSessionEvent({ type: 'mohist_prompt', payload: { text: 'hello' } })]
      reconstructRoundsFromEvents(events)
      expect(spy).toHaveBeenCalledWith(events, 'timeline')
    } finally {
      spy.mockRestore()
    }
  })

  it('creates one round per mohist_prompt with assistant and thought content grouped', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'mohist_prompt', payload: { text: 'first prompt', kind: 'initial' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({ sequence: 1, type: 'agent_message_chunk', payload: { text: 'Hello' }, createdAt: '2024-01-01T00:00:01Z' }),
      makeSessionEvent({ sequence: 2, type: 'agent_message_chunk', payload: { text: ' world' }, createdAt: '2024-01-01T00:00:02Z' }),
      makeSessionEvent({ sequence: 3, type: 'agent_thought_chunk', payload: { text: 'thinking' }, createdAt: '2024-01-01T00:00:03Z' }),
      makeSessionEvent({ sequence: 4, type: 'mohist_prompt', payload: { text: 'second prompt', kind: 'task' }, createdAt: '2024-01-01T00:00:04Z' }),
      makeSessionEvent({ sequence: 5, type: 'agent_message_chunk', payload: { text: 'second' }, createdAt: '2024-01-01T00:00:05Z' }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(2)
    expect(rounds[0].roundIndex).toBe(0)
    expect(rounds[0].userText).toBe('first prompt')
    expect(rounds[0].agentText).toBe('Hello world')
    expect(rounds[0].thoughtText).toBe('thinking')
    expect(rounds[0].startedAt).toBe('2024-01-01T00:00:00Z')
    expect(rounds[1].roundIndex).toBe(1)
    expect(rounds[1].userText).toBe('second prompt')
    expect(rounds[1].agentText).toBe('second')
    expect(rounds[1].thoughtText).toBe('')
  })

  it('groups tool_call and tool_call_update by toolCallId with updated status', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'mohist_prompt', payload: { text: 'use tools' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({ sequence: 1, type: 'tool_call', payload: { toolCallId: 'call-1', kind: 'bash', title: 'bash', rawInput: '{"command":"ls"}' }, createdAt: '2024-01-01T00:00:01Z' }),
      makeSessionEvent({ sequence: 2, type: 'tool_call_update', payload: { toolCallId: 'call-1', status: 'completed', rawOutput: 'file.txt' }, createdAt: '2024-01-01T00:00:02Z' }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(1)
    expect(rounds[0].toolCalls).toHaveLength(1)
    expect(rounds[0].toolCalls[0].toolCallId).toBe('call-1')
    expect(rounds[0].toolCalls[0].toolName).toBe('bash')
    expect(rounds[0].toolCalls[0].state).toBe('completed')
    expect(rounds[0].toolCalls[0].rawOutput).toBe('file.txt')
    expect(rounds[0].toolCalls[0].rawInput).toBe('{"command":"ls"}')
  })

  it('maps agent_liveness_status events to recovery events on the active round', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'mohist_prompt', payload: { text: 'p' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({ sequence: 1, type: 'agent_liveness_status', payload: { status: 'probing', activeProbeVersion: 2 }, createdAt: '2024-01-01T00:00:01Z' }),
      makeSessionEvent({ sequence: 2, type: 'agent_liveness_status', payload: { status: 'failed', failureReason: 'timeout' }, createdAt: '2024-01-01T00:00:02Z' }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(1)
    expect(rounds[0].recoveryEvents).toHaveLength(2)
    expect(rounds[0].recoveryEvents[0].status).toBe('recovering')
    expect(rounds[0].recoveryEvents[0].attempt).toBe(2)
    expect(rounds[0].recoveryEvents[1].status).toBe('failed')
    expect(rounds[0].recoveryEvents[1].reason).toBe('timeout')
  })

  it('infers round labels from total count', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'mohist_prompt', payload: { text: 'p1' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({ sequence: 1, type: 'mohist_prompt', payload: { text: 'p2' }, createdAt: '2024-01-01T00:00:01Z' }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds[0].label).toBe('proposal.md')
    expect(rounds[1].label).toBe('specs/')
  })
})
