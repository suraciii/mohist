import { describe, it, expect } from 'vitest'
import { screen, fireEvent, waitFor } from '../../../../tests/test-utils'
import { SessionTranscriptView } from './SessionTranscriptView'
import { installSessionTranscriptViewFixture } from './SessionTranscriptView.fixture'
import { renderWithQueryClient, makeTurn } from '../../../../tests/session-page-test-utils'
import type { ToolPart } from '../../../entities/coder-session'

installSessionTranscriptViewFixture()

describe('SessionTranscriptView', () => {
  describe('todowrite summary', () => {
    it('renders todowrite as Updated todo list by default', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'todowrite',
              status: 'completed',
              input: '{"todos":[{"content":"Task 1","status":"completed"},{"content":"Task 2","status":"pending"}]}',
              output: '{"todos":[{"content":"Task 1","status":"completed"},{"content":"Task 2","status":"pending"}]}',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Updated todo list/i)).toBeInTheDocument()
      })
      expect(screen.getByText(/\(2 items\)/i)).toBeInTheDocument()
    })

    it('renders normalized todowrite summary when toolName is unknown', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-1',
            toolName: 'unknown',
            normalizedName: 'todowrite',
            status: 'completed',
            input: '{"todos":[{"content":"Task 1","status":"completed"}]}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Updated todo list/i)).toBeInTheDocument()
      })
    })

    it('expands todowrite to show tool details', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'todowrite',
              status: 'completed',
              input: '{"todos":[{"content":"Task 1","status":"completed"}]}',
              output: '{}',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Updated todo list/i)).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText(/Updated todo list/i))

      await waitFor(() => {
        expect(screen.getByText(/src\/index\.ts|Task 1/i)).toBeInTheDocument()
      }, { timeout: 3000 })
    })

    it('renders failed todowrite with failure indicator', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'todowrite',
              status: 'failed',
              input: '{"todos":[]}',
              error: 'Failed to update todos',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Updated todo list/i)).toBeInTheDocument()
      })
      expect(screen.getByText(/failed/i)).toBeInTheDocument()
    })
  })
})
