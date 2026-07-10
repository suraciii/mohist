import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, fireEvent, waitFor } from '../../../../tests/test-utils'
import { SessionTranscriptView } from './SessionTranscriptView'
import type { SessionTurn, TextPart, ReasoningPart, ToolPart, ErrorPart } from '../../../entities/coder-session'
import { renderWithQueryClient, makeTurn, queryClients } from '../../../../tests/session-page-test-utils'
import { setScopedValue } from '../../../../tests/support/scoped-property'

beforeEach(() => {
  vi.clearAllMocks()
  setScopedValue(navigator, 'clipboard', { writeText: vi.fn().mockResolvedValue(undefined) })
  setScopedValue(Element.prototype, 'scrollTo', vi.fn())
})

afterEach(() => {
  vi.useRealTimers()
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.length = 0
})

describe('SessionTranscriptView', () => {
  describe('prompt card expansion and copy', () => {
    it('renders Mohist prompt card with kind and timestamp', async () => {
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: 'Implement feature X',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Task')).toBeInTheDocument()
      })
      expect(screen.getAllByText(/10:00:00|2024/).length).toBeGreaterThan(0)
    })

    it('expands long prompt when Show full prompt is clicked', async () => {
      const longText = 'A'.repeat(600)
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: longText,
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Show full prompt')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText('Show full prompt'))

      await waitFor(() => {
        expect(screen.getByText('Show less')).toBeInTheDocument()
      })
    })

    it('keeps raw prompt collapsed by default even when it is short', async () => {
      const rawPrompt = '<mohist-task><role>Implement fix</role><contract>proposal.md</contract></mohist-task>'
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: rawPrompt,
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
          summary: {
            kind: 'task',
            title: 'Implement fix',
            subtitle: 'Output: proposal.md',
            outputPath: 'proposal.md',
          },
        },
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Implement fix')).toBeInTheDocument()
      })
      expect(screen.getByText('Show full prompt')).toBeInTheDocument()
      expect(screen.queryByText(rawPrompt)).not.toBeInTheDocument()

      fireEvent.click(screen.getByText('Show full prompt'))

      await waitFor(() => {
        expect(screen.getByText(rawPrompt)).toBeInTheDocument()
      })
    })

    it('copies prompt text when Copy button is clicked', async () => {
      const mockWriteText = vi.fn().mockResolvedValue(undefined)
      setScopedValue(navigator, 'clipboard', { writeText: mockWriteText })

      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: 'Copy me',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Copy')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText('Copy'))

      await waitFor(() => {
        expect(mockWriteText).toHaveBeenCalledWith('Copy me')
        expect(screen.getByText('Copied!')).toBeInTheDocument()
      })
    })
  })

  describe('markdown assistant rendering', () => {
    it('renders markdown text with proper formatting', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: '# Heading\n\nSome **bold** text\n\n- List item\n\n```js\nconsole.log("code")\n```',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Heading')).toBeInTheDocument()
      })
      expect(screen.getByText('bold')).toBeInTheDocument()
      expect(screen.getByText('List item')).toBeInTheDocument()
      expect(screen.getByText('console.log("code")')).toBeInTheDocument()
    })

    it('renders inline code with proper styling', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Use `const x = 1` for constants',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        const codeElement = screen.getByText('const x = 1')
        expect(codeElement.tagName).toBe('CODE')
      })
    })
  })

  describe('collapsed reasoning', () => {
    it('renders reasoning as collapsed details with size and timestamp', async () => {
      const reasoningText = 'This is my thinking process...'.repeat(100)
      const turns = [makeTurn({
        assistant: [{
          id: 'reasoning-1',
          type: 'reasoning',
          text: reasoningText,
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as ReasoningPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Thinking\.\.\./i)).toBeInTheDocument()
      })

      const summary = screen.getByText(/Thinking\.\.\./i).closest('details')?.querySelector('summary')
      expect(summary).toBeInTheDocument()
    })

    it('expands reasoning when clicked', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'reasoning-1',
          type: 'reasoning',
          text: 'Detailed reasoning content',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as ReasoningPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Thinking\.\.\./i)).toBeInTheDocument()
      })

      const details = screen.getByText(/Thinking\.\.\./i).closest('details')
      if (details) {
        fireEvent.click(details.querySelector('summary')!)
        await waitFor(() => {
          expect(screen.getByText('Detailed reasoning content')).toBeInTheDocument()
        })
      }
    })
  })

  describe('generic unknown tool rendering', () => {
    it('renders unknown tool with generic fallback card', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-unknown',
            toolName: 'UnknownTool',
            status: 'completed',
            title: 'Unknown Tool',
            input: '{"arg1":"value1"}',
            output: '{"result":"ok"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/UnknownTool/)).toBeInTheDocument()
      })
    })

    it('renders unknown tool with description as subtitle when no displayTitle or displaySubtitle', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-unknown',
            toolName: 'CustomTool',
            status: 'completed',
            input: '{"description":"This is a useful description"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/CustomTool/)).toBeInTheDocument()
      })
      expect(screen.getByText(/This is a useful description/)).toBeInTheDocument()
    })

    it('renders unknown tool with url as subtitle when no displayTitle or displaySubtitle', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-unknown',
            toolName: 'WebFetch',
            status: 'completed',
            input: '{"url":"https://example.com/api/data"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/WebFetch/)).toBeInTheDocument()
      })
      expect(screen.getByText(/https:\/\/example\.com\/api\/data/)).toBeInTheDocument()
    })

    it('renders unknown tool with query as subtitle when no displayTitle or displaySubtitle', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-unknown',
            toolName: 'SearchTool',
            status: 'completed',
            input: '{"query":"find something"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/SearchTool/)).toBeInTheDocument()
      })
      expect(screen.getByText(/find something/)).toBeInTheDocument()
    })

    it('renders unknown tool with filePath as subtitle when no displayTitle or displaySubtitle', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-unknown',
            toolName: 'ReadTool',
            status: 'completed',
            input: '{"file_path":"src/main.ts"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/ReadTool/)).toBeInTheDocument()
      })
      expect(screen.getByText(/src\/main\.ts/)).toBeInTheDocument()
    })

    it('renders read tool with human-readable label from getToolLabel', async () => {
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
            output: 'file content',
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

    it('renders grep tool with human-readable args', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-grep',
            normalizedName: 'grep',
            toolName: 'grep',
            status: 'completed',
            input: '{"pattern":"function foo","type":"typescript","scope":"src"}',
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

    it('renders reasoning as collapsed details element by default', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'reasoning-1',
          type: 'reasoning',
          text: 'Detailed reasoning content'.repeat(50),
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as ReasoningPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Thinking\.\.\./i)).toBeInTheDocument()
      })

      const details = screen.getByText(/Thinking\.\.\./i).closest('details')
      expect(details).toBeInTheDocument()
      const summary = details?.querySelector('summary')
      expect(summary).toBeInTheDocument()
      const content = details?.querySelector('pre')
      expect(content).not.toBeInTheDocument()
    })

    it('expands reasoning when summary is clicked', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'reasoning-1',
          type: 'reasoning',
          text: 'Detailed reasoning content',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as ReasoningPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Thinking\.\.\./i)).toBeInTheDocument()
      })

      const details = screen.getByText(/Thinking\.\.\./i).closest('details')
      if (details) {
        fireEvent.click(details.querySelector('summary')!)
        await waitFor(() => {
          expect(screen.getByText('Detailed reasoning content')).toBeInTheDocument()
        })
      }
    })

    it('renders bash tool with human-readable command label', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-bash',
            normalizedName: 'bash',
            toolName: 'bash',
            status: 'completed',
            input: '{"command":"npm test","cwd":"/project"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/npm test/)).toBeInTheDocument()
      })
    })

    it('renders question tool with human-readable query subtitle', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-question',
            normalizedName: 'question',
            toolName: 'question',
            status: 'completed',
            input: '{"question":"Should I use React or Vue?"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Should I use React or Vue\?/)).toBeInTheDocument()
      })
    })

    it('renders webfetch tool with url subtitle', async () => {
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

    it('renders task tool with description subtitle', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-task',
            normalizedName: 'task',
            toolName: 'task',
            status: 'completed',
            input: '{"description":"Implement feature X"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Implement feature X/)).toBeInTheDocument()
      })
    })

    it('renders skill tool with name subtitle', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-skill',
            normalizedName: 'skill',
            toolName: 'skill',
            status: 'completed',
            input: '{"name":"frontend-design"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/frontend-design/)).toBeInTheDocument()
      })
    })

    it('does not display unknown label for tools with raw toolName but no displayTitle', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-foo',
            toolName: 'FooTool',
            normalizedName: 'FooTool',
            status: 'completed',
            input: '{"arg1":"value1"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.queryByText(/unknown/i)).not.toBeInTheDocument()
      })
    })
  })

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
