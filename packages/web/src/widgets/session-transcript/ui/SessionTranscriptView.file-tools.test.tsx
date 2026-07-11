import { describe, it, expect } from 'vitest'
import { screen, fireEvent, waitFor } from '../../../../tests/test-utils'
import { SessionTranscriptView } from './SessionTranscriptView'
import { installSessionTranscriptViewFixture } from './SessionTranscriptView.fixture'
import { renderWithQueryClient, makeTurn } from '../../../../tests/session-page-test-utils'
import type { ToolPart } from '../../../entities/coder-session'

installSessionTranscriptViewFixture()

describe('SessionTranscriptView', () => {
  describe('file-changing tool rendering', () => {
    it('renders apply_patch showing file summary', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-apply-patch',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** Add File: src/new-file.ts\n+++ b/src/new-file.ts\n@@ -0,0 +1,2 @@\n+line 1\n+line 2' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
      expect(screen.queryByText(/@@ -0,0 \+1,2 @@/i)).not.toBeInTheDocument()
    })

    it('renders apply_patch with title=apply_patch without toolName', async () => {
      const patchText = `*** Add File: src/brand-new.ts
+++ b/src/brand-new.ts
@@ -0,0 +1 @@
+new content`

      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-patch',
            toolName: 'unknown',
            title: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
    })

    it('renders normalized apply_patch as file summary when toolName is unknown and title is a file', async () => {
      const patchText = `*** Add File: src/normalized.ts
+++ b/src/normalized.ts
@@ -0,0 +1 @@
+new content`

      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-patch',
            toolName: 'unknown',
            normalizedName: 'apply_patch',
            title: 'src/normalized.ts',
            status: 'completed',
            input: JSON.stringify({ patchText }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
      expect(screen.queryByText('unknown')).not.toBeInTheDocument()
      expect(screen.queryByText('src/normalized.ts')).not.toBeInTheDocument()
    })

    it('renders write as created file with file name', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-write',
            toolName: 'write',
            status: 'completed',
            input: JSON.stringify({ path: 'src/created.ts', content: 'line 1\nline 2\nline 3' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Created')).toBeInTheDocument()
      })
      expect(screen.getByText(/created\.ts/i)).toBeInTheDocument()
    })

    it('renders edit with modified file', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit',
            toolName: 'edit',
            status: 'completed',
            input: JSON.stringify({ file_path: 'src/example.ts', old_string: 'old', new_string: 'new content' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Edited')).toBeInTheDocument()
      })
      expect(screen.getByText(/example\.ts/i)).toBeInTheDocument()
    })

    it('expands raw patch when Show raw patch is clicked', async () => {
      const patchText = `*** Add File: src/test.ts
+++ b/src/test.ts
@@ -0,0 +1 @@
+test`

      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-apply-patch',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })

      const showRawButton = screen.getByText(/Show raw patch/i)
      fireEvent.click(showRawButton)

      await waitFor(() => {
        expect(screen.getByText(/@@ -0,0 \+1 @@/i)).toBeInTheDocument()
      })
    })

    it('renders failed file-changing tool with error', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit-fail',
            toolName: 'edit',
            status: 'failed',
            input: JSON.stringify({ file_path: 'src/failing.ts', old_string: 'old', new_string: 'new' }),
            error: 'File not found',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Edited')).toBeInTheDocument()
      })
      expect(screen.getByText(/File not found/i)).toBeInTheDocument()
    })

    it('renders delete operation with deleted file', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-delete',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** Delete File: src/deleted.ts\n--- a/src/deleted.ts\n+++ b/src/deleted.ts\n@@ -1 +0,0 @@\n-old content' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
      expect(screen.getByText(/deleted\.ts/i)).toBeInTheDocument()
    })

    it('renders moved file with new path', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-move',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** OldPath: src/old-location.ts\n*** Move to: src/new-location.ts' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
      expect(screen.getByText(/new-location\.ts/i)).toBeInTheDocument()
    })
  })
})
