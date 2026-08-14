import { describe, expect, it } from 'vitest'
import { deriveTimelineItems } from './derive'
import type { TimelineFact } from './types'

function fact(overrides: Partial<TimelineFact> & Pick<TimelineFact, 'kind' | 'sourceId' | 'order'>): TimelineFact {
  return {
    source: 'transcript',
    occurredAt: '2026-08-03T10:00:00.000Z',
    raw: { sourceId: overrides.sourceId },
    ...overrides,
  }
}

describe('deriveTimelineItems', () => {
  it('derives every render class from explicit facts', () => {
    const items = deriveTimelineItems([
      fact({ sourceId: 'input', order: 1, kind: 'input', input: { text: 'Ship it', acceptance: 'accepted' } }),
      fact({ sourceId: 'message', order: 2, kind: 'message', text: 'Done' }),
      fact({ sourceId: 'reasoning', order: 3, kind: 'reasoning', text: 'Checking' }),
      fact({ sourceId: 'read', order: 4, kind: 'tool', tool: { callId: 'read', name: 'read', input: { path: 'a.ts' }, status: 'completed' } }),
      fact({ sourceId: 'edit', order: 5, kind: 'tool', tool: { callId: 'edit', name: 'edit', target: 'a.ts', status: 'completed', changedFiles: [{ path: 'a.ts', additions: 12, deletions: 3 }] } }),
      fact({ sourceId: 'shell', order: 6, kind: 'tool', tool: { callId: 'shell', name: 'bash', command: 'npm test', exitCode: 0, status: 'completed' } }),
      fact({ sourceId: 'domain', order: 7, kind: 'tool', tool: { callId: 'domain', name: 'bash', command: 'mo issue start 42', exitCode: 0, status: 'completed' } }),
      fact({ sourceId: 'plan', order: 8, kind: 'plan' }),
      fact({ sourceId: 'tool', order: 9, kind: 'tool', tool: { callId: 'tool', name: 'custom', status: 'completed' } }),
      fact({ sourceId: 'status', order: 10, kind: 'status', status: { state: '执行中' } }),
      fact({ sourceId: 'boundary', order: 11, kind: 'boundary', boundary: { kind: 'reset' } }),
      fact({ sourceId: 'error', order: 12, kind: 'error', error: { message: '网络失败' } }),
      fact({ sourceId: 'unknown', order: 13, kind: 'unknown', text: '未知事件' }),
      fact({ sourceId: 'suppressed', order: 14, kind: 'suppressed' }),
    ])

    expect(items.map(item => item.renderClass)).toEqual([
      'input', 'message', 'reasoning', 'file-read', 'file-edit', 'shell', 'domain-action',
      'plan', 'tool', 'status', 'boundary', 'error', 'unknown', 'suppressed',
    ])
    expect(items.map(item => item.salience)).toEqual([
      'normal', 'normal', 'low', 'low', 'medium', 'medium', 'high',
      'normal', 'low', 'quiet', 'normal', 'critical', 'normal', 'quiet',
    ])
    expect(items[2]).toMatchObject({
      summary: '进行了思考',
      salience: 'low',
      detail: { raw: { sourceId: 'reasoning' } },
    })
    expect(items[4]).toMatchObject({ summary: '编辑了 a.ts (+12/-3) → 通过', salience: 'medium' })
    expect(items[5]).toMatchObject({ salience: 'medium' })
    expect(items[6]).toMatchObject({
      summary: '启动了 #42 → 通过',
      reference: { kind: 'issue', issueNumber: 42 },
    })
  })

  it('turns failures into a single error item while retaining the action sentence', () => {
    const [item] = deriveTimelineItems([
      fact({
        sourceId: 'failed-comment',
        order: 1,
        kind: 'tool',
        tool: { callId: 'comment', name: 'bash', command: 'mo issue comment create 42', exitCode: 1, status: 'failed' },
      }),
    ])

    expect(item).toMatchObject({ renderClass: 'error', summary: '评论了 #42 → 失败', salience: 'critical' })
  })

  it('updates a tool in place and never regresses after a terminal fact', () => {
    const items = deriveTimelineItems([
      fact({ sourceId: 'tool-start', order: 1, kind: 'tool', tool: { callId: 'same-tool', name: 'bash', command: 'npm test', input: { command: 'npm test' }, status: 'running' } }),
      fact({ sourceId: 'tool-complete', order: 2, kind: 'tool', tool: { callId: 'same-tool', name: 'bash', output: 'ok', exitCode: 0, status: 'completed' } }),
      fact({ sourceId: 'tool-late', order: 3, kind: 'tool', tool: { callId: 'same-tool', name: 'bash', status: 'running' } }),
    ])

    expect(items).toHaveLength(1)
    expect(items[0]).toMatchObject({ id: 'same-tool', renderClass: 'shell', summary: '运行了 npm test → 通过', isTerminal: true })
    expect(items[0]?.sourceIds).toEqual(['tool-start', 'tool-complete'])
    expect(items[0]?.detail).toMatchObject({ input: { command: 'npm test' }, output: 'ok' })
  })

  it('appends stream chunks until a non-text fact seals the stream', () => {
    const items = deriveTimelineItems([
      fact({ sourceId: 'message-1', order: 1, kind: 'message', correlationId: 'message', text: 'first ' }),
      fact({ sourceId: 'message-2', order: 2, kind: 'message', correlationId: 'message', text: 'part' }),
      fact({ sourceId: 'tool', order: 3, kind: 'tool', tool: { callId: 'read', name: 'read', status: 'completed' } }),
      fact({ sourceId: 'message-3', order: 4, kind: 'message', correlationId: 'message', text: 'after tool' }),
    ])

    expect(items.map(item => item.summary)).toEqual(['回复了 first part', '读取了 文件 → 通过', '回复了 after tool'])
    expect(items[0]?.isStreaming).toBe(false)
  })
})
