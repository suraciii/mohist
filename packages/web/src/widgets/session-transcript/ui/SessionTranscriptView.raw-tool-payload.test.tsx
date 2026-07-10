import { describe, it, expect } from 'vitest'
import { screen, fireEvent, waitFor } from '../../../../tests/test-utils'
import { SessionTranscriptView } from './SessionTranscriptView'
import { installSessionTranscriptViewFixture } from './SessionTranscriptView.fixture'
import { renderWithQueryClient, makeTurn } from '../../../../tests/session-page-test-utils'
import type { SessionTurn, ToolPart } from '../../../entities/coder-session'

installSessionTranscriptViewFixture()

describe('SessionTranscriptView', () => {
  describe('raw tool payload disclosure', () => {
    it('exposes raw input, raw output, metadata, and details on a bash tool through the disclosure', async () => {
      const rawInput = JSON.stringify({ command: 'npm test', cwd: '/project' })
      const rawOutput = JSON.stringify({ stdout: 'ok', exitCode: 1 })
      const metadata = { toolName: 'bash', childSessionId: null }
      const details = { family: 'execution', cwd: '/project', exitCode: 1, outputPreview: 'ok' }

      const turns: SessionTurn[] = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-1',
            normalizedName: 'bash',
            toolName: 'bash',
            status: 'completed',
            input: rawInput,
            output: 'ok',
            rawInput,
            rawOutput,
            metadata,
            details,
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      const { container } = renderWithQueryClient(
        <SessionTranscriptView turns={turns} isRunning={false} />,
      )

      await waitFor(() => {
        expect(screen.getByText(/npm test/)).toBeInTheDocument()
      })

      const text = container.textContent ?? ''
      expect(text).toContain('npm test')
      expect(text).toContain('ok')
    })

    it('exposes raw input, raw output, and details on an edit tool through the disclosure', async () => {
      const rawInput = JSON.stringify({ file_path: 'src/app.ts', old_string: 'old', new_string: 'new' })
      const rawOutput = 'old\nnew'
      const metadata = { toolName: 'edit' }
      const details = { family: 'mutation', files: [] }

      const turns: SessionTurn[] = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-1',
            normalizedName: 'edit',
            toolName: 'edit',
            displayTitle: 'app.ts',
            status: 'completed',
            input: rawInput,
            output: rawOutput,
            rawInput,
            rawOutput,
            metadata,
            details,
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      const { container } = renderWithQueryClient(
        <SessionTranscriptView turns={turns} isRunning={false} />,
      )

      await waitFor(() => {
        expect(screen.getByText('app.ts')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText('app.ts'))

      await waitFor(() => {
        const text = container.textContent ?? ''
        expect(text).toContain('app.ts')
      })

      const showRaw = screen.queryByText(/Show raw patch/i)
      if (showRaw) {
        fireEvent.click(showRaw)

        await waitFor(() => {
          const text = container.textContent ?? ''
          expect(text).toContain(rawInput)
          expect(text).toContain(rawOutput)
        })
      }
    })
  })
})
