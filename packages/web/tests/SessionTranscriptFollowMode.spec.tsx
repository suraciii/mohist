import { describe, it, expect, vi } from 'vitest'
import { screen, waitFor, act } from './test-utils'
import { SessionTranscriptView } from '../src/widgets/session-transcript/ui/SessionTranscriptView'
import { useSessionTranscript } from '../src/widgets/session-transcript/model/useSessionTranscript'
import { dispatchAgentEvent } from '../src/entities/agent'
import type { TextPart } from '../src/entities/coder-session'
import { renderWithQueryClient, renderHookWithQueryClient, makeTurn } from './session-page-test-utils'
import { setScopedValue } from './support/scoped-property'
import { installSessionTranscriptViewFixture } from '../src/widgets/session-transcript/ui/SessionTranscriptView.fixture'

installSessionTranscriptViewFixture()

describe('Follow-mode scrolling and streaming text pacing', () => {
  describe('follow-mode pause/resume', () => {
    it('auto-scrolls when reader is near bottom during live session', async () => {
      const scrollToMock = vi.fn()
      setScopedValue(Element.prototype, 'scrollTo', scrollToMock)

      const initialTurns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Initial content',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: '2024-01-01T10:00:02.000Z',
        } as TextPart],
      })]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        runtimeSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(true))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          text: ' More content',
          sessionId: 'session-123',
        })
      })

      await waitFor(() => {
        expect(result.current.newContentAvailable).toBe(false)
      })
      expect(scrollToMock).not.toHaveBeenCalled()
    })

    it('does not auto-scroll when user scrolls away from bottom', async () => {
      const scrollToMock = vi.fn()
      setScopedValue(Element.prototype, 'scrollTo', scrollToMock)

      const initialTurns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Initial content',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: '2024-01-01T10:00:02.000Z',
        } as TextPart],
      })]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        runtimeSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(false))
      await waitFor(() => expect(result.current.isNearBottom).toBe(false))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          text: ' More content',
          sessionId: 'session-123',
        })
      })

      expect(scrollToMock).not.toHaveBeenCalled()
    })

    it('restores follow mode when scrollToBottom is called', async () => {
      const scrollToMock = vi.fn()
      setScopedValue(Element.prototype, 'scrollTo', scrollToMock)

      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        runtimeSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(false))
      await waitFor(() => expect(result.current.isNearBottom).toBe(false))

      act(() => {
        result.current.scrollToBottom()
      })

      expect(result.current.isNearBottom).toBe(true)
      expect(result.current.newContentAvailable).toBe(false)
      expect(scrollToMock).not.toHaveBeenCalled()
    })
  })

  describe('new-content indicator behavior', () => {
    it('sets newContentAvailable when not near bottom and content arrives', async () => {
      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        runtimeSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(false))
      await waitFor(() => expect(result.current.isNearBottom).toBe(false))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          text: 'New streaming content',
          sessionId: 'session-123',
        })
      })

      await waitFor(() => {
        expect(result.current.newContentAvailable).toBe(true)
      })
    })

    it('does not set newContentAvailable when near bottom', async () => {
      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        runtimeSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(true))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          text: 'New streaming content',
          sessionId: 'session-123',
        })
      })

      expect(result.current.newContentAvailable).toBe(false)
    })

    it('clears newContentAvailable when acknowledgeNewContent is called', async () => {
      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        runtimeSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(false))
      await waitFor(() => expect(result.current.isNearBottom).toBe(false))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          text: 'New content',
          sessionId: 'session-123',
        })
      })

      await waitFor(() => {
        expect(result.current.newContentAvailable).toBe(true)
      })

      act(() => {
        result.current.acknowledgeNewContent()
      })

      expect(result.current.newContentAvailable).toBe(false)
    })
  })

  describe('nested scrollable regions', () => {
    it('does not toggle follow mode when scrolling within nested scrollable region', async () => {
      const scrollToMock = vi.fn()
      setScopedValue(Element.prototype, 'scrollTo', scrollToMock)

      const initialTurns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Content with code block',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: '2024-01-01T10:00:02.000Z',
        } as TextPart],
      })]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        runtimeSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(true))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          text: ' More content',
          sessionId: 'session-123',
        })
      })

      await waitFor(() => {
        expect(result.current.newContentAvailable).toBe(false)
      })
      expect(scrollToMock).not.toHaveBeenCalled()
    })
  })

  describe('streaming text pacing', () => {
    it('shows blinking cursor for incomplete text part', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Streaming text',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={true} />)

      await waitFor(() => {
        expect(screen.getByText('Streaming text')).toBeInTheDocument()
      })

      const cursor = document.querySelector('span.animate-pulse')
      expect(cursor).toBeInTheDocument()
    })

    it('does not show blinking cursor for completed text part', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Completed text',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: '2024-01-01T10:00:02.000Z',
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Completed text')).toBeInTheDocument()
      })

      const cursors = document.querySelectorAll('span.animate-pulse')
      const textCursors = Array.from(cursors).filter(cursor => {
        const parent = cursor.parentElement
        return parent?.textContent?.includes('Completed text')
      })
      expect(textCursors).toHaveLength(0)
    })

    it('persisted transcript content is unchanged by pacing display', async () => {
      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        runtimeSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          text: 'First chunk',
          sessionId: 'session-123',
        })
      })

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          text: ' second chunk',
          sessionId: 'session-123',
        })
      })

      await waitFor(() => {
        const textPart = result.current.turns.at(-1)?.assistant.find(
          (p): p is TextPart => p.type === 'text',
        )
        expect(textPart?.text).toBe('First chunk second chunk')
      })
    })
  })
})
