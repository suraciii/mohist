import { describe, it, expect } from 'vitest'
import { screen, waitFor } from './test-utils'
import { SessionTranscriptView } from '../src/widgets/session-transcript/ui/SessionTranscriptView'
import type { ToolPart } from '../src/entities/coder-session'
import { renderWithQueryClient, makeTurn } from './session-page-test-utils'
import { installSessionTranscriptViewFixture } from '../src/widgets/session-transcript/ui/SessionTranscriptView.fixture'

installSessionTranscriptViewFixture()

describe('ToolRegistry', () => {
  describe('fallback behavior', () => {
    it('renders unknown tool using registry fallback entry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-unknown',
            toolName: 'SomeUnknownTool',
            status: 'completed',
            input: '{"arg1":"value1","description":"A custom tool"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/SomeUnknownTool/)).toBeInTheDocument()
      })
      expect(screen.queryByText(/^unknown$/i)).not.toBeInTheDocument()
    })

    it('falls back to raw toolName when no parsing signals available', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-fallback',
            toolName: 'MyCustomTool',
            status: 'completed',
            input: '{"foo":"bar"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/MyCustomTool/)).toBeInTheDocument()
      })
    })
  })

  describe('known tool-family renderer selection', () => {
    it('renders bash tool with command label from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-bash',
            normalizedName: 'bash',
            toolName: 'bash',
            status: 'completed',
            input: '{"command":"npm run build","cwd":"/project"}',
            output: 'build success',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/npm run build/)).toBeInTheDocument()
      })
    })

    it('renders read tool with file path label from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-read',
            normalizedName: 'read',
            toolName: 'Read',
            status: 'completed',
            input: '{"file_path":"src/index.ts"}',
            output: 'file content here',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/src\/index\.ts/)).toBeInTheDocument()
      })
    })

    it('renders grep tool with pattern label from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-grep',
            normalizedName: 'grep',
            toolName: 'grep',
            status: 'completed',
            input: '{"pattern":"function foo","type":"typescript"}',
            output: 'found 3 matches',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/function foo/)).toBeInTheDocument()
      })
    })

    it('renders webfetch tool with url subtitle from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-webfetch',
            normalizedName: 'webfetch',
            toolName: 'webfetch',
            status: 'completed',
            input: '{"url":"https://api.example.com/data","method":"GET"}',
            output: '{"data":"result"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/https:\/\/api\.example\.com\/data/)).toBeInTheDocument()
      })
    })

    it('renders question tool with query subtitle from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-question',
            normalizedName: 'question',
            toolName: 'question',
            status: 'completed',
            input: '{"question":"Which approach is better?"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Which approach is better\?/)).toBeInTheDocument()
      })
    })

    it('renders task tool with description subtitle from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-task',
            normalizedName: 'task',
            toolName: 'task',
            status: 'completed',
            input: '{"description":"Run tests on CI"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Run tests on CI/)).toBeInTheDocument()
      })
    })

    it('renders skill tool with name subtitle from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-skill',
            normalizedName: 'skill',
            toolName: 'skill',
            status: 'completed',
            input: '{"name":"debugging-code"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/debugging-code/)).toBeInTheDocument()
      })
    })

    it('renders edit tool with file name from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit',
            normalizedName: 'edit',
            toolName: 'edit',
            status: 'completed',
            input: '{"file_path":"src/app.ts","oldString":"foo","newString":"bar"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/app\.ts/)).toBeInTheDocument()
      })
    })

    it('renders write tool with file name from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-write',
            normalizedName: 'write',
            toolName: 'write',
            status: 'completed',
            input: '{"path":"src/new-file.ts","content":"hello world"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/new-file\.ts/)).toBeInTheDocument()
      })
    })

    it('renders apply_patch tool with file summary from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-patch',
            normalizedName: 'apply_patch',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** Add File: src/brand-new.ts\n+++ b/src/brand-new.ts\n@@ -0,0 +1 @@\n+new content' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
    })
  })
})
