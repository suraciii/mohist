import { describe, it, expect, vi } from 'vitest'
import { screen, fireEvent, waitFor, act, within } from './test-utils'
import { SessionTranscriptView } from '../src/widgets/session-transcript/ui/SessionTranscriptView'
import type { TextPart } from '../src/entities/coder-session'
import { renderWithQueryClient, makeTurn, getAssistantCopyButton, expandChangedFilesTool } from './session-page-test-utils'
import { setScopedValue } from './support/scoped-property'
import { installSessionTranscriptViewFixture } from '../src/widgets/session-transcript/ui/SessionTranscriptView.fixture'

installSessionTranscriptViewFixture()

describe('Transcript affordances', () => {
  describe('assistant reply copy action', () => {
    it('shows copy button on assistant text part', async () => {
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
      expect(getAssistantCopyButton()).toBeInTheDocument()
    })

    it('copies assistant text when copy button is clicked', async () => {
      const mockWriteText = vi.fn().mockResolvedValue(undefined)
      setScopedValue(navigator, 'clipboard', { writeText: mockWriteText })

      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Copy this text',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      expect(getAssistantCopyButton()).toBeInTheDocument()

      fireEvent.click(getAssistantCopyButton())

      await waitFor(() => {
        expect(mockWriteText).toHaveBeenCalledWith('Copy this text')
        expect(screen.getByText('Copied!')).toBeInTheDocument()
      })
    })

    it('copy button shows Copy again after timeout', async () => {
      vi.useFakeTimers()
      const mockWriteText = vi.fn().mockResolvedValue(undefined)
      setScopedValue(navigator, 'clipboard', { writeText: mockWriteText })

      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Test text',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      expect(getAssistantCopyButton()).toBeInTheDocument()

      fireEvent.click(getAssistantCopyButton())

      await act(async () => {
        await Promise.resolve()
      })

      expect(mockWriteText).toHaveBeenCalledWith('Test text')
      expect(screen.getByText('Copied!')).toBeInTheDocument()

      act(() => {
        vi.advanceTimersByTime(2000)
      })

      expect(mockWriteText).toHaveBeenCalledWith('Test text')
      expect(getAssistantCopyButton()).toBeInTheDocument()

      vi.useRealTimers()
    })
  })

  describe('expanded diff inspection', () => {
    it('shows expanded diff view when rawDetail is available', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-diff',
            toolName: 'edit',
            status: 'completed',
            input: JSON.stringify({ file_path: 'src/test.ts', old_string: 'old', new_string: 'new' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
            changedFiles: [
              {
                path: 'src/test.ts',
                operation: 'modified',
                additions: 1,
                deletions: 1,
                rawDetail: '--- a/src/test.ts\n+++ b/src/test.ts\n@@ -1 +1 @@\n-old\n+new',
              },
            ],
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
      expandChangedFilesTool()
      expect(screen.getByText('src/test.ts')).toBeInTheDocument()
      expect(screen.getByText('+1')).toBeInTheDocument()
      expect(screen.getByText('-1')).toBeInTheDocument()
    })

    it('hides diff content by default and shows it when expanded', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-diff',
            toolName: 'edit',
            status: 'completed',
            input: JSON.stringify({ file_path: 'src/test.ts', old_string: 'old', new_string: 'new' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
            changedFiles: [
              {
                path: 'src/test.ts',
                operation: 'modified',
                additions: 1,
                deletions: 1,
                rawDetail: '--- a/src/test.ts\n+++ b/src/test.ts\n@@ -1 +1 @@\n-old\n+new',
              },
            ],
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
      expandChangedFilesTool()
      expect(screen.getByText('src/test.ts')).toBeInTheDocument()

      expect(screen.queryByText(/--- a\/src\/test.ts/)).not.toBeInTheDocument()

      fireEvent.click(screen.getByText(/Show raw patch/i))

      await waitFor(() => {
        expect(within(screen.getByText('Changes').closest('div')!.parentElement!).getByText(/old/)).toBeInTheDocument()
        expect(within(screen.getByText('Changes').closest('div')!.parentElement!).getByText(/new/)).toBeInTheDocument()
      })
    })

    it('shows file summary with additions/deletions when rawDetail is not available', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-diff',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** Add File: src/new.ts\n+++ b/src/new.ts\n@@ -0,0 +1 @@\n+line1' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
      expandChangedFilesTool()
      expect(screen.getByText('src/new.ts')).toBeInTheDocument()
      expect(screen.getByText('+1')).toBeInTheDocument()
      expect(screen.getByText(/Show raw patch/i)).toBeInTheDocument()
    })

    it('shows rawDetail content in diff view when available', async () => {
      const rawDetailContent = '--- a/src/app.ts\n+++ b/src/app.ts\n@@ -1,3 +1,3 @@\n const x = 1\n-const y = 2\n+const y = 3\n const z = 4'

      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit',
            toolName: 'edit',
            status: 'completed',
            input: JSON.stringify({ file_path: 'src/app.ts', old_string: 'const y = 2', new_string: 'const y = 3' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
            changedFiles: [
              {
                path: 'src/app.ts',
                operation: 'modified',
                additions: 1,
                deletions: 1,
                rawDetail: rawDetailContent,
              },
            ],
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
      expandChangedFilesTool()
      expect(screen.getByText('src/app.ts')).toBeInTheDocument()

      fireEvent.click(screen.getByText(/Show raw patch/i))

      await waitFor(() => {
        expect(screen.getAllByText(/const y = 2/).length).toBeGreaterThan(0)
        expect(screen.getAllByText(/const y = 3/).length).toBeGreaterThan(0)
      })
    })
  })
})
