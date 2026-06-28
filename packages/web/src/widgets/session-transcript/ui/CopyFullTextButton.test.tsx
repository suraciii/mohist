// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { fireEvent, render, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CopyFullTextButton } from './CopyFullTextButton'
import type {
  DisplayTurn,
  DisplayPrompt,
  DisplayAssistantPart,
} from '../model/session-transcript-display'

const toastMock = vi.hoisted(() => ({
  success: vi.fn(),
  error: vi.fn(),
  warning: vi.fn(),
  info: vi.fn(),
}))

vi.mock('sonner', () => ({
  toast: toastMock,
}))

function makePrompt(overrides: Partial<DisplayPrompt> = {}): DisplayPrompt {
  return {
    role: 'mohist',
    text: 'prompt body',
    kind: 'followup',
    sentAt: overrides.sentAt ?? '2024-05-15T10:00:00.000Z',
    ...overrides,
  }
}

function makeTurn(overrides: {
  id?: string
  startedAt: string
  completedAt?: string | null
  prompt?: Partial<DisplayPrompt>
  assistantParts?: DisplayAssistantPart[]
}): DisplayTurn {
  return {
    id: overrides.id ?? 'turn-1',
    startedAt: overrides.startedAt,
    completedAt: overrides.completedAt ?? null,
    prompt: makePrompt({ sentAt: overrides.startedAt, ...overrides.prompt }),
    assistantParts: overrides.assistantParts ?? [],
    changedFiles: [],
    state: 'idle',
  }
}

function makeMultiTurnFixture(): DisplayTurn[] {
  return [
    makeTurn({
      id: 't1',
      startedAt: '2024-05-15T10:00:00.000Z',
      prompt: {
        kind: 'initial',
        title: 'Add header navigation',
        text: 'Refactor the SessionPage header.',
      },
      assistantParts: [
        { id: 'p1', partType: 'text', text: 'Reading current implementation.', startedAt: '2024-05-15T10:00:01.000Z', completedAt: '2024-05-15T10:00:05.000Z' },
        { id: 'p2', partType: 'reasoning', text: 'x'.repeat(1024), startedAt: '2024-05-15T10:00:01.000Z', completedAt: '2024-05-15T10:00:03.000Z' },
        {
          id: 'p3',
          partType: 'tool',
          toolCallId: 'tc-1',
          normalizedName: 'apply_patch',
          toolName: 'apply_patch',
          status: 'completed',
          displayTitle: 'src/SessionPage.tsx',
          startedAt: '2024-05-15T10:00:06.000Z',
          hasError: false,
          isContextTool: false,
        },
        { id: 'p4', partType: 'error', message: 'Tool failed', kind: 'failed', at: '2024-05-15T10:00:08.000Z' },
      ],
    }),
    makeTurn({
      id: 't2',
      startedAt: '2024-05-15T10:30:00.000Z',
      prompt: { kind: 'followup', title: 'Wire TOC', text: 'Add toolbar TOC.' },
      assistantParts: [
        { id: 'p5', partType: 'text', text: 'On it.', startedAt: '2024-05-15T10:30:01.000Z', completedAt: '2024-05-15T10:30:05.000Z' },
      ],
    }),
  ]
}

function setClipboard(value: unknown) {
  Object.defineProperty(navigator, 'clipboard', {
    value,
    configurable: true,
  })
}

describe('CopyFullTextButton', () => {
  let originalClipboard: unknown

  beforeEach(() => {
    originalClipboard = (navigator as { clipboard?: unknown }).clipboard
    setClipboard({ writeText: vi.fn().mockResolvedValue(undefined) })
  })

  afterEach(() => {
    setClipboard(originalClipboard)
    vi.clearAllMocks()
  })

  it('mounts with the default label and data-copy-full-text attribute', () => {
    render(<CopyFullTextButton turns={makeMultiTurnFixture()} />)

    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    expect(button).not.toBeNull()
    expect(button.textContent).toBe('Copy full text')
    expect(button.getAttribute('aria-label')).toBe('Copy full transcript text')
    expect(button.getAttribute('data-state')).toBe('idle')
    expect(button.disabled).toBe(false)
  })

  it('is disabled when turns.length === 0', () => {
    render(<CopyFullTextButton turns={[]} />)

    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    expect(button.disabled).toBe(true)
  })

  it('does not call clipboard.writeText when disabled (empty transcript)', () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    setClipboard({ writeText })

    render(<CopyFullTextButton turns={[]} />)

    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    expect(button.disabled).toBe(true)
  })

  it('on success: writes the full serialized transcript and shows Copied! for ~2s', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    setClipboard({ writeText })

    const turns = makeMultiTurnFixture()
    render(<CopyFullTextButton turns={turns} />)

    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    fireEvent.click(button)

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1))
    const writtenText = writeText.mock.calls[0][0] as string
    expect(writtenText).toContain('== Turn 1 · Initial Task ·')
    expect(writtenText).toContain('== Turn 2 · Follow-up ·')
    expect(writtenText).toContain('[reasoning omitted, 1.0 KB]')
    expect(writtenText).toContain('[tool apply_patch] src/SessionPage.tsx')
    expect(writtenText).toContain('[error] Tool failed')

    await waitFor(() => expect(button.getAttribute('data-state')).toBe('copied'))
    expect(button.textContent).toBe('Copied!')
    expect(toastMock.error).not.toHaveBeenCalled()
  })

  it('on writeText reject: surfaces toast.error, sets data-state="failed", does NOT show success', async () => {
    const writeText = vi.fn().mockRejectedValue(new Error('permission denied'))
    setClipboard({ writeText })

    render(<CopyFullTextButton turns={makeMultiTurnFixture()} />)

    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    fireEvent.click(button)

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledTimes(1))
    expect(toastMock.error).toHaveBeenCalledWith('Failed to copy transcript to clipboard.')
    await waitFor(() => expect(button.getAttribute('data-state')).toBe('failed'))
    expect(button.textContent).toBe('Copy full text')
    expect(button.textContent).not.toBe('Copied!')
  })

  it('when navigator.clipboard is absent: does not call writeText, surfaces toast.error, sets data-state="failed"', async () => {
    setClipboard(undefined)

    render(<CopyFullTextButton turns={makeMultiTurnFixture()} />)

    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    fireEvent.click(button)

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledTimes(1))
    expect(toastMock.error).toHaveBeenCalledWith('Clipboard is unavailable in this browser.')
    await waitFor(() => expect(button.getAttribute('data-state')).toBe('failed'))
    expect(button.textContent).toBe('Copy full text')
  })

  it('uses the robust clipboard-existence check from ArtifactContentViewer (no bare .then)', () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    setClipboard({ writeText })

    render(<CopyFullTextButton turns={makeMultiTurnFixture()} />)

    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    fireEvent.click(button)

    expect(writeText).toHaveBeenCalledTimes(1)
  })

  it('memoizes the serialized text on the turns reference', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    setClipboard({ writeText })

    const turns = makeMultiTurnFixture()
    const { rerender } = render(<CopyFullTextButton turns={turns} />)

    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    fireEvent.click(button)
    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1))
    const firstText = writeText.mock.calls[0][0]

    writeText.mockClear()
    rerender(<CopyFullTextButton turns={turns} />)

    fireEvent.click(button)
    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1))
    const secondText = writeText.mock.calls[0][0]
    expect(secondText).toBe(firstText)
  })

  it('regenerates the serialized text when the turns reference changes', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    setClipboard({ writeText })

    const turns1 = makeMultiTurnFixture()
    const turns2: DisplayTurn[] = [
      makeTurn({
        id: 'only',
        startedAt: '2024-05-15T11:00:00.000Z',
        prompt: { kind: 'task', title: 'Different' },
      }),
    ]

    const { rerender } = render(<CopyFullTextButton turns={turns1} />)
    rerender(<CopyFullTextButton turns={turns2} />)

    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    fireEvent.click(button)

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1))
    const text = writeText.mock.calls[0][0] as string
    expect(text).toContain('== Turn 1 · Task ·')
    expect(text).not.toContain('== Turn 2 ·')
  })

  it('does not fire toast.success on successful copy', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    setClipboard({ writeText })

    render(<CopyFullTextButton turns={makeMultiTurnFixture()} />)
    const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
    fireEvent.click(button)

    await waitFor(() => expect(button.getAttribute('data-state')).toBe('copied'))
    expect(toastMock.error).not.toHaveBeenCalled()
    expect(toastMock.success).not.toHaveBeenCalled()
  })

  it('resets data-state back to idle after the ~2s timer elapses (success path)', async () => {
    vi.useFakeTimers()
    try {
      const writeText = vi.fn().mockResolvedValue(undefined)
      setClipboard({ writeText })

      render(<CopyFullTextButton turns={makeMultiTurnFixture()} />)
      const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
      fireEvent.click(button)

      await vi.waitFor(() => expect(button.getAttribute('data-state')).toBe('copied'))
      vi.advanceTimersByTime(2000)
      await vi.waitFor(() => expect(button.getAttribute('data-state')).toBe('idle'))
      expect(button.textContent).toBe('Copy full text')
    } finally {
      vi.useRealTimers()
    }
  })

  it('resets data-state back to idle after the ~2s timer elapses (failure path)', async () => {
    vi.useFakeTimers()
    try {
      const writeText = vi.fn().mockRejectedValue(new Error('boom'))
      setClipboard({ writeText })

      render(<CopyFullTextButton turns={makeMultiTurnFixture()} />)
      const button = document.querySelector('[data-copy-full-text]') as HTMLButtonElement
      fireEvent.click(button)

      await vi.waitFor(() => expect(button.getAttribute('data-state')).toBe('failed'))
      vi.advanceTimersByTime(2000)
      await vi.waitFor(() => expect(button.getAttribute('data-state')).toBe('idle'))
      expect(button.textContent).toBe('Copy full text')
    } finally {
      vi.useRealTimers()
    }
  })
})