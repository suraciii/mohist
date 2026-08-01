import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { IdentityPreviewStep } from './identity-preview-step'

describe('IdentityPreviewStep', () => {
  it('renders the derived identity preview alongside the Create in Slack link', () => {
    render(
      <IdentityPreviewStep
        botName="preview-bot"
        appDescription="Derived from the bound Agent."
        slackAppCreationReference="https://api.slack.com/apps?new_app=1"
      />,
    )

    expect(screen.getByTestId('connection-setup-identity-bot-name')).toHaveTextContent('preview-bot')
    expect(screen.getByTestId('connection-setup-identity-app-description')).toHaveTextContent(
      'Derived from the bound Agent.',
    )

    const link = screen.getByTestId('connection-setup-create-in-slack')
    expect(link).toHaveAttribute('href', 'https://api.slack.com/apps?new_app=1')
    expect(link).toHaveAttribute('target', '_blank')
  })

  it('renders the avatar configuration note pointing to Slack settings', () => {
    render(
      <IdentityPreviewStep
        botName="preview-bot"
        appDescription="A description."
        slackAppCreationReference="https://api.slack.com/apps"
      />,
    )

    const note = screen.getByTestId('connection-setup-identity-avatar-note')
    expect(note.textContent ?? '').toMatch(/avatar/i)
    expect(note.textContent ?? '').toMatch(/slack app settings/i)
  })
})
