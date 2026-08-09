import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { AgentAvatar } from './AgentAvatar'

describe('AgentAvatar', () => {
  it('tries a corrected image source after the previous source failed', () => {
    const view = render(
      <AgentAvatar
        agentName="Test Agent"
        avatar="https://example.test/broken.png"
        className="size-12"
        iconClassName="size-6"
        testId="test-avatar"
      />,
    )

    fireEvent.error(screen.getByRole('img', { name: 'Test Agent avatar' }))
    expect(screen.getByTestId('test-avatar')).toHaveAttribute('data-avatar-state', 'fallback')

    view.rerender(
      <AgentAvatar
        agentName="Test Agent"
        avatar="https://example.test/corrected.png"
        className="size-12"
        iconClassName="size-6"
        testId="test-avatar"
      />,
    )

    expect(screen.getByRole('img', { name: 'Test Agent avatar' })).toHaveAttribute(
      'src',
      'https://example.test/corrected.png',
    )
    expect(screen.getByTestId('test-avatar')).toHaveAttribute('data-avatar-state', 'image')
  })
})
