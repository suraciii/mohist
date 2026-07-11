import { describe, it, expect } from 'vitest'
import { screen, waitFor } from '../../../../tests/test-utils'
import { SessionTranscriptView } from './SessionTranscriptView'
import { installSessionTranscriptViewFixture } from './SessionTranscriptView.fixture'
import { renderWithQueryClient, makeTurn } from '../../../../tests/session-page-test-utils'
import type { SessionTurn, TextPart, ErrorPart } from '../../../entities/coder-session'

installSessionTranscriptViewFixture()

describe('SessionTranscriptView', () => {
  describe('error part rendering', () => {
    it('renders error part with message', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'error-1',
          type: 'error',
          message: 'Execution failed',
          kind: 'failed',
          at: '2024-01-01T10:00:05.000Z',
        } as ErrorPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Execution failed/i)).toBeInTheDocument()
      })
    })
  })

  describe('empty and loading states', () => {
    it('shows no activity message when turns are empty and not running', () => {
      renderWithQueryClient(<SessionTranscriptView turns={[]} isRunning={false} />)
      expect(screen.getByText(/No activity recorded/i)).toBeInTheDocument()
    })

    it('shows waiting message when turns are empty and running', () => {
      renderWithQueryClient(<SessionTranscriptView turns={[]} isRunning={true} />)
      expect(screen.getByText(/Waiting for activity/i)).toBeInTheDocument()
    })
  })

  describe('turn rendering', () => {
    it('renders Mohist speaker label with timestamp', async () => {
      const turns = [makeTurn({
        startedAt: '2024-01-01T10:00:00.000Z',
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Mohist')).toBeInTheDocument()
      })
    })

    it('shows incomplete marker for legacy missing prompts', async () => {
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: 'Prompt was not recorded for this historical session',
          kind: 'legacy-missing',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        incomplete: true,
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Missing Prompt/i)).toBeInTheDocument()
      })
      expect(screen.getByText(/Incomplete/i)).toBeInTheDocument()
    })

    it('renders Coder speaker label when assistant parts exist', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Hello world',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Coder')).toBeInTheDocument()
      })
    })

    it('renders legacy missing prompt with gray styling and no expand', async () => {
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: '',
          kind: 'legacy-missing',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        incomplete: true,
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Missing Prompt/i)).toBeInTheDocument()
      })
      expect(screen.queryByText('Show full prompt')).not.toBeInTheDocument()
    })

    it('legacy-missing turn does not use task title as prompt body and omits Show full prompt', async () => {
      const shortTaskTitle = 'Cover backend projection and progress behavior'
      const sessionIdLabel = 'T-005.1'
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: 'Prompt was not recorded for this historical session',
          kind: 'legacy-missing',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        incomplete: true,
      })]

      const { container } = renderWithQueryClient(
        <SessionTranscriptView turns={turns} isRunning={false} />,
      )

      await waitFor(() => {
        expect(screen.getByText(/Missing Prompt/i)).toBeInTheDocument()
      })

      const promptBodies = screen.getAllByText(/Prompt was not recorded/i)
      expect(promptBodies.length).toBeGreaterThanOrEqual(1)

      const text = container.textContent ?? ''
      expect(text).not.toContain(shortTaskTitle)
      expect(text).not.toContain(sessionIdLabel)
      expect(screen.queryByText('Show full prompt')).not.toBeInTheDocument()
      expect(screen.queryByText(shortTaskTitle)).not.toBeInTheDocument()
      expect(screen.queryByText(sessionIdLabel)).not.toBeInTheDocument()
    })

    it('renders two turns in event order when fed two mohist_prompt events', async () => {
      const firstPrompt = 'First prompt for T-005.1 — initialize the transcript model'
      const firstTitle = 'Initialize transcript'
      const secondPrompt = 'Second prompt for T-005.1 — continue with the legacy fallback'
      const secondTitle = 'Continue legacy fallback'

      const turns: SessionTurn[] = [
        makeTurn({
          id: 'turn-1',
          startedAt: '2024-01-01T10:00:00.000Z',
          completedAt: '2024-01-01T10:00:30.000Z',
          user: {
            role: 'mohist',
            text: firstPrompt,
            kind: 'task',
            sentAt: '2024-01-01T10:00:00.000Z',
            summary: {
              kind: 'task',
              title: firstTitle,
            },
          },
          assistant: [{
            id: 'text-1',
            type: 'text',
            text: 'First assistant response',
            startedAt: '2024-01-01T10:00:01.000Z',
            completedAt: '2024-01-01T10:00:02.000Z',
          } as TextPart],
        }),
        makeTurn({
          id: 'turn-2',
          startedAt: '2024-01-01T10:00:30.000Z',
          completedAt: '2024-01-01T10:01:00.000Z',
          user: {
            role: 'mohist',
            text: secondPrompt,
            kind: 'task',
            sentAt: '2024-01-01T10:00:30.000Z',
            summary: {
              kind: 'task',
              title: secondTitle,
            },
          },
          assistant: [{
            id: 'text-2',
            type: 'text',
            text: 'Second assistant response',
            startedAt: '2024-01-01T10:00:31.000Z',
            completedAt: '2024-01-01T10:00:32.000Z',
          } as TextPart],
        }),
      ]

      const { container } = renderWithQueryClient(
        <SessionTranscriptView turns={turns} isRunning={false} />,
      )

      await waitFor(() => {
        expect(screen.getByText(firstTitle)).toBeInTheDocument()
        expect(screen.getByText(secondTitle)).toBeInTheDocument()
      })

      const firstTitleEl = screen.getByText(firstTitle)
      const secondTitleEl = screen.getByText(secondTitle)
      const position = firstTitleEl.compareDocumentPosition(secondTitleEl)
      expect(position & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()

      const text = container.textContent ?? ''
      const firstIdx = text.indexOf(firstTitle)
      const secondIdx = text.indexOf(secondTitle)
      expect(firstIdx).toBeGreaterThanOrEqual(0)
      expect(secondIdx).toBeGreaterThan(firstIdx)

      const allShowFull = screen.getAllByText('Show full prompt')
      expect(allShowFull.length).toBe(2)

      const allCoder = screen.getAllByText('Coder')
      expect(allCoder.length).toBe(2)
    })
  })
})
