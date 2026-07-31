import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { PromptBlock } from './PromptBlock'

describe('PromptBlock attachment rendering', () => {
  it('resolves att references through the supplied scoped attachment resolver', () => {
    const resolveAttachment = vi.fn(() => ({
      url: '/api/projects/proj-1/agent-sessions/session-1/inputs/input-1/attachments/att-1/content',
      contentType: 'text/plain',
      fileName: 'notes.txt',
      size: 3,
    }))

    render(
      <PromptBlock
        prompt={{
          role: 'mohist',
          text: 'Read [notes.txt](att:att-1)',
          kind: 'followup',
          sentAt: '2026-01-01T00:00:00.000Z',
        }}
        resolveAttachment={resolveAttachment}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Show full prompt' }))

    expect(resolveAttachment).toHaveBeenCalledWith('att-1')
    expect(screen.getByTestId('markdown-attachment-file-card')).toHaveAttribute(
      'href',
      '/api/projects/proj-1/agent-sessions/session-1/inputs/input-1/attachments/att-1/content',
    )
  })
})
