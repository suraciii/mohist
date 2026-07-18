import type { DisplayToolPart, DisplayTurn } from './session-transcript-display'
import { selectFailedToolCalls } from './select-failed-tool-calls'

const tool = (id: string, status: DisplayToolPart['status']): DisplayToolPart => ({
  id, partType: 'tool', toolCallId: id, normalizedName: 'bash', toolName: 'bash', status,
  startedAt: '', completedAt: null, hasError: status === 'failed', isContextTool: false,
})

const turn = (parts: DisplayTurn['assistantParts']): DisplayTurn => ({
  id: 'turn', startedAt: '', completedAt: null,
  prompt: { role: 'mohist', text: '', kind: 'initial', sentAt: '' },
  assistantParts: parts, changedFiles: [], state: 'idle',
})

describe('selectFailedToolCalls', () => {
  it('returns failed tools in document order, including groups', () => {
    const first = tool('first', 'failed')
    const second = tool('second', 'completed')
    const third = tool('third', 'failed')
    expect(selectFailedToolCalls([turn([first, { id: 'group', partType: 'context-group', title: '', tools: [second, third], hasError: true }])])).toEqual([first, third])
  })
})
