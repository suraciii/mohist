import { describe, it, expect } from 'vitest'
import { screen, fireEvent, waitFor } from '../../../../tests/test-utils'
import { SessionTranscriptView } from './SessionTranscriptView'
import { installSessionTranscriptViewFixture } from './SessionTranscriptView.fixture'
import { renderWithQueryClient, makeTurn } from '../../../../tests/session-page-test-utils'
import type { TextPart, ToolPart } from '../../../entities/coder-session'

installSessionTranscriptViewFixture()

describe('SessionTranscriptView', () => {
  describe('context tool grouping', () => {
    it('groups consecutive context tools into Gathering context card', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/index.ts"}',
              output: 'file content',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
          {
            id: 'tool-2',
            type: 'tool',
            tool: {
              toolCallId: 'tc-2',
              toolName: 'grep',
              status: 'completed',
              input: '{"pattern":"function"}',
              output: 'matches',
              startedAt: '2024-01-01T10:00:04.000Z',
              completedAt: '2024-01-01T10:00:05.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Gathering context/i)).toBeInTheDocument()
      })
    })

    it('expands context group to show individual tools', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/index.ts"}',
              output: 'content',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
          {
            id: 'tool-2',
            type: 'tool',
            tool: {
              toolCallId: 'tc-2',
              toolName: 'glob',
              status: 'completed',
              input: '{"pattern":"**/*.ts"}',
              output: 'files',
              startedAt: '2024-01-01T10:00:04.000Z',
              completedAt: '2024-01-01T10:00:05.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Gathering context/i)).toBeInTheDocument()
      })
      expect(screen.queryByText('content')).not.toBeInTheDocument()

      fireEvent.click(screen.getByText(/Context gathered/i))

      await waitFor(() => {
        expect(screen.getByText(/src\/index\.ts/i)).toBeInTheDocument()
      }, { timeout: 3000 })
    })

    it('does not group across text or reasoning boundaries', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'text-1',
            type: 'text',
            text: 'Let me check the files first',
            startedAt: '2024-01-01T10:00:01.000Z',
            completedAt: null,
          } as TextPart,
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/index.ts"}',
              output: 'content',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Let me check the files first/i)).toBeInTheDocument()
      })
      expect(screen.getByText(/Gathering context/i)).toBeInTheDocument()
    })

    it('shows failed count in context group summary', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'read',
              status: 'failed',
              input: '{"file_path":"missing.txt"}',
              error: 'File not found',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
          {
            id: 'tool-2',
            type: 'tool',
            tool: {
              toolCallId: 'tc-2',
              toolName: 'grep',
              status: 'completed',
              input: '{"pattern":"function"}',
              output: 'matches',
              startedAt: '2024-01-01T10:00:04.000Z',
              completedAt: '2024-01-01T10:00:05.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Gathering context/)).toBeInTheDocument()
      })
    })

    it('groups tools using normalizedName when toolName is unknown', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-1',
            toolName: 'unknown',
            normalizedName: 'read',
            status: 'completed',
            input: '{"file_path":"src/index.ts"}',
            output: 'content',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Gathering context/i)).toBeInTheDocument()
      })
      expect(screen.queryByText('unknown')).not.toBeInTheDocument()
    })
  })
})
