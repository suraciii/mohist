import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { AgentAvatar } from './AgentAvatar'

describe('AgentAvatar', () => {
  it('ignores a late error from a previous source or agent identity', () => {
    const view = render(
      <AgentAvatar
        agentName="Test Agent"
        avatar="https://example.test/broken.png"
        className="size-12"
        iconClassName="size-6"
        testId="test-avatar"
      />,
    )

    const previousImage = screen.getByRole('img', { name: 'Test Agent avatar' })
    fireEvent.error(previousImage)
    expect(screen.getByTestId('test-avatar')).toHaveAttribute('data-avatar-state', 'fallback')

    view.rerender(
      <AgentAvatar
        agentName="Corrected Agent"
        avatar="https://example.test/corrected.png"
        className="size-12"
        iconClassName="size-6"
        testId="test-avatar"
      />,
    )

    const currentImage = screen.getByRole('img', { name: 'Corrected Agent avatar' })
    expect(currentImage).toHaveAttribute(
      'src',
      'https://example.test/corrected.png',
    )
    expect(screen.getByTestId('test-avatar')).toHaveAttribute('data-avatar-state', 'image')

    fireEvent.error(previousImage)

    expect(screen.getByRole('img', { name: 'Corrected Agent avatar' })).toBe(currentImage)
    expect(screen.getByTestId('test-avatar')).toHaveAttribute('data-avatar-state', 'image')
  })
})
