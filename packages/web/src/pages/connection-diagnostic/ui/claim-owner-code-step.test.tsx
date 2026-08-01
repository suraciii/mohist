import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ClaimOwnerCodeStep } from './claim-owner-code-step'

afterEach(cleanup)

describe('ClaimOwnerCodeStep', () => {
  it('shows the empty-state when no code is set', () => {
    render(
      <ClaimOwnerCodeStep
        code={null}
        expiresAt={null}
        onGenerate={() => undefined}
        isGenerating={false}
        errorMessage={null}
      />,
    )

    expect(screen.getByTestId('connection-setup-claim-owner-empty')).toBeInTheDocument()
    expect(screen.getByTestId('connection-setup-claim-owner-generate')).toHaveTextContent(/generate/i)
  })

  it('renders the code and expiry once a code is present', () => {
    render(
      <ClaimOwnerCodeStep
        code="CLAIM-CODE-1"
        expiresAt="2026-08-01T01:00:00.000Z"
        onGenerate={() => undefined}
        isGenerating={false}
        errorMessage={null}
      />,
    )

    expect(screen.getByTestId('connection-setup-claim-owner-code')).toHaveTextContent('CLAIM-CODE-1')
    expect(screen.getByTestId('connection-setup-claim-owner-expires-at')).toHaveTextContent('2026')
  })

  it('regenerating calls onGenerate again, invalidating the previous code server-side', async () => {
    const user = userEvent.setup()
    const onGenerate = vi.fn()
    render(
      <ClaimOwnerCodeStep
        code="CLAIM-CODE-1"
        expiresAt="2026-08-01T01:00:00.000Z"
        onGenerate={onGenerate}
        isGenerating={false}
        errorMessage={null}
      />,
    )

    await user.click(screen.getByTestId('connection-setup-claim-owner-generate'))

    expect(onGenerate).toHaveBeenCalledTimes(1)
    expect(screen.getByTestId('connection-setup-claim-owner-generate')).toHaveTextContent(/regenerate/i)
  })

  it('disables the button while generating', () => {
    render(
      <ClaimOwnerCodeStep
        code={null}
        expiresAt={null}
        onGenerate={() => undefined}
        isGenerating={true}
        errorMessage={null}
      />,
    )

    const button = screen.getByTestId('connection-setup-claim-owner-generate')
    expect(button).toBeDisabled()
  })

  it('renders the error message when provided', () => {
    render(
      <ClaimOwnerCodeStep
        code={null}
        expiresAt={null}
        onGenerate={() => undefined}
        isGenerating={false}
        errorMessage="claim_unavailable"
      />,
    )

    expect(screen.getByTestId('connection-setup-claim-owner-error')).toHaveTextContent('claim_unavailable')
  })
})
