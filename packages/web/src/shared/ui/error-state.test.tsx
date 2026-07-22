import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ErrorState } from './error-state'

afterEach(cleanup)

describe('ErrorState', () => {
  it('renders the default title, message, and Retry button when onRetry is provided', () => {
    render(<ErrorState onRetry={() => {}} />)

    expect(screen.getByTestId('error-state')).toBeTruthy()
    expect(screen.getByTestId('error-state-title').textContent).toBe('Something went wrong')
    expect(screen.getByTestId('error-state-message').textContent).toBe('We could not load this page. Please try again.')
    expect(screen.getByTestId('error-state-retry').textContent).toBe('Retry')
  })

  it('renders custom title, message, and retry label', () => {
    render(
      <ErrorState
        title="Failed to load issue"
        message="Server busy"
        retryLabel="Try again"
        onRetry={() => {}}
      />,
    )

    expect(screen.getByTestId('error-state-title').textContent).toBe('Failed to load issue')
    expect(screen.getByTestId('error-state-message').textContent).toBe('Server busy')
    expect(screen.getByTestId('error-state-retry').textContent).toBe('Try again')
  })

  it('invokes onRetry when the Retry button is clicked', () => {
    const onRetry = vi.fn()
    render(<ErrorState onRetry={onRetry} />)

    fireEvent.click(screen.getByTestId('error-state-retry'))

    expect(onRetry).toHaveBeenCalledTimes(1)
  })

  it('does not render a Retry button when onRetry is omitted', () => {
    render(<ErrorState />)

    expect(screen.queryByTestId('error-state-retry')).toBeNull()
  })

  it('uses semantic danger tokens for its container (theme-tokenized, no literal palette)', () => {
    render(<ErrorState onRetry={() => {}} />)

    const container = screen.getByTestId('error-state')
    const card = container.querySelector('div')
    expect(card).toBeTruthy()
    const className = card?.className ?? ''
    expect(className).toContain('border-danger-border')
    expect(className).toContain('bg-danger-subtle')
    expect(className).not.toMatch(/border-(red|amber|gray|blue)-\d/)
    expect(className).not.toMatch(/bg-(red|amber|gray|blue)-\d/)
  })

  it('marks itself as visually distinct from the not-found state via data-error-state="transient"', () => {
    render(<ErrorState onRetry={() => {}} />)

    const container = screen.getByTestId('error-state')
    expect(container.getAttribute('data-error-state')).toBe('transient')
  })
})
