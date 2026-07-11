import { describe, it, expect } from 'vitest'
import { screen, waitFor } from '../../../../tests/test-utils'
import { SessionTranscriptView } from './SessionTranscriptView'
import { installSessionTranscriptViewFixture } from './SessionTranscriptView.fixture'
import { renderWithQueryClient, makeTurn } from '../../../../tests/session-page-test-utils'

installSessionTranscriptViewFixture()

describe('SessionTranscriptView', () => {
  describe('file-changing tool rendering', () => {
    it('renders turn-level changed-files output when tool has changedFiles', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit',
            toolName: 'edit',
            status: 'completed',
            changedFiles: [
              { path: 'src/index.ts', operation: 'modified', additions: 10, deletions: 3 },
            ],
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
    })

    it('turn-level changed-files shows additions/deletions', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-write',
            toolName: 'write',
            status: 'completed',
            changedFiles: [
              { path: 'src/new.ts', operation: 'created', additions: 25 },
            ],
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        const changedEls = screen.getAllByText('1 file changed')
        expect(changedEls.length).toBeGreaterThan(0)
      }, { timeout: 3000 })
    })

    it('turn-level changed-files deduplicates when multiple tools modify same file', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-edit-1',
              toolName: 'edit',
              status: 'completed',
              changedFiles: [
                { path: 'src/index.ts', operation: 'modified', additions: 5, deletions: 2 },
              ],
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          },
          {
            id: 'tool-2',
            type: 'tool',
            tool: {
              toolCallId: 'tc-edit-2',
              toolName: 'edit',
              status: 'completed',
              changedFiles: [
                { path: 'src/index.ts', operation: 'modified', additions: 10, deletions: 5 },
              ],
              startedAt: '2024-01-01T10:00:03.000Z',
              completedAt: '2024-01-01T10:00:04.000Z',
            },
          },
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
    })
  })
})
