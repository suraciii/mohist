import { describe, expect, it } from 'vitest'
import type { AgentTranscriptDetail } from '../../../entities/agent'
import type { SessionPart, SessionTurn } from '../../../entities/coder-session'
import { buildTimelineFacts } from './timeline-facts'

const at = '2026-08-03T10:00:00.000Z'

function turn(id: string, assistant: SessionPart[] = []): SessionTurn {
  return {
    id,
    startedAt: at,
    completedAt: null,
    user: { role: 'mohist', text: `prompt-${id}`, kind: 'task', sentAt: at },
    assistant,
  }
}

describe('buildTimelineFacts', () => {
  it('keeps raw transcript parts and links multiple inputs by inputIds', () => {
    const tool: SessionPart = {
      id: 'part-tool',
      type: 'tool',
      tool: {
        toolCallId: 'tool-1',
        toolName: 'bash',
        status: 'completed',
        rawInput: { command: 'npm test' } as never,
        rawOutput: { exitCode: 0, stdout: 'ok' } as never,
        startedAt: at,
        completedAt: at,
      },
    }
    const transcriptTurn = turn('turn-1', [tool])
    const facts = buildTimelineFacts({
      turns: [transcriptTurn],
      summary: {
        inputs: [
          { id: 'input-1', sequence: 1, source: 'web', acceptance: 'accepted' },
          { id: 'input-2', sequence: 2, source: 'web', acceptance: 'accepted' },
        ],
        turns: [{ id: 'turn-1', sequence: 3, inputIds: ['input-1', 'input-2'], status: 'executing' }],
        activity: 'active',
      },
    })

    expect(facts.filter(fact => fact.kind === 'input')).toMatchObject([
      { sourceId: 'input:input-1', input: { text: 'prompt-turn-1', acceptance: 'accepted', turnId: 'turn-1' } },
      { sourceId: 'input:input-2', input: { text: '消息', acceptance: 'accepted', turnId: 'turn-1' } },
    ])
    expect(facts.find(fact => fact.sourceId === 'part:part-tool')).toMatchObject({
      kind: 'tool',
      raw: tool,
      tool: {
        callId: 'tool-1',
        input: { command: 'npm test' },
        output: { exitCode: 0, stdout: 'ok' },
        status: 'completed',
      },
    })
    expect(facts).toContainEqual(expect.objectContaining({
      sourceId: 'turn:turn-1:state',
      kind: 'status',
      status: { label: '执行中', state: 'executing', turnId: 'turn-1' },
    }))
  })

  it('shows terminal turn results and context boundaries as separate facts', () => {
    const reset: SessionPart = {
      id: 'reset',
      type: 'error',
      kind: 'context-reset',
      message: 'runtime reset',
      at,
    }
    const facts = buildTimelineFacts({
      turns: [turn('turn-failed', [reset])],
      summary: {
        turns: [{
          id: 'turn-failed',
          sequence: 2,
          inputIds: [],
          status: 'failed',
          result: { failureReason: 'provider unavailable' },
        }],
        recoveryHistory: [{ type: 'compaction', recordedAt: '2026-08-03T10:00:02.000Z', summary: 'summary' }],
      },
    })

    expect(facts).toContainEqual(expect.objectContaining({
      sourceId: 'part:reset',
      kind: 'boundary',
      boundary: { kind: 'reset', reason: 'runtime reset' },
    }))
    expect(facts).toContainEqual(expect.objectContaining({
      sourceId: 'turn:turn-failed:result',
      kind: 'error',
      error: { message: 'provider unavailable', kind: 'failed' },
    }))
    expect(facts).toContainEqual(expect.objectContaining({
      sourceId: 'recovery:compaction:2026-08-03T10:00:02.000Z:0',
      kind: 'boundary',
      boundary: { kind: 'compaction', summary: 'summary' },
    }))
    expect(facts.findIndex(fact => fact.sourceId === 'part:reset')).toBeLessThan(
      facts.findIndex(fact => fact.sourceId === 'recovery:compaction:2026-08-03T10:00:02.000Z:0'),
    )
  })

  it('keeps live header/payload raw and deduplicates repeated source ids without collapsing updates', () => {
    const started: AgentTranscriptDetail = {
      type: 'tool_call.started',
      sourceId: 'event-start',
      sequence: 7,
      createdAt: at,
      payload: { raw: true },
      sessionId: 'session-1',
      runtimeSessionId: 'runtime-1',
      runtime: 'opencode',
    }
    const completed: AgentTranscriptDetail = {
      ...started,
      sourceId: 'event-complete',
      sequence: 8,
      payload: { raw: 'complete' },
    }
    const facts = buildTimelineFacts({
      liveDetails: [started, started, completed],
      summary: { activity: 'active' },
      lastActivityAt: at,
    })

    expect(facts.filter(fact => fact.source === 'live')).toHaveLength(2)
    expect(facts[0]?.raw).toBe(started)
    expect(facts[1]?.raw).toBe(completed)
    expect(facts[0]).toMatchObject({ sourceId: 'event-start', order: 7_000_001, kind: 'tool' })
    expect(facts[1]).toMatchObject({ sourceId: 'event-complete', order: 8_000_002, kind: 'tool' })
  })

  it('keeps an input independent with unknown acceptance when no association is proven', () => {
    const facts = buildTimelineFacts({
      turns: [turn('turn-unmatched')],
      summary: {
        inputs: [{ id: 'input-unmatched', sequence: 1, source: 'web', acceptance: 'unknown' }],
        turns: [],
      },
    })

    expect(facts).toContainEqual(expect.objectContaining({
      sourceId: 'input:input-unmatched',
      input: { text: '消息', acceptance: 'unknown', turnId: undefined },
    }))
    expect(facts).toContainEqual(expect.objectContaining({
      sourceId: 'turn:turn-unmatched:input',
      input: { text: 'prompt-turn-unmatched', acceptance: 'unknown' },
    }))
  })
})
