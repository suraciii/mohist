import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'

import { Button, buttonVariants } from './button'

describe('shared/ui Button', () => {
  it('renders an enabled primary button with the variant background and foreground', () => {
    render(<Button>Save</Button>)

    const button = screen.getByRole('button', { name: 'Save' })
    expect(button).toBeEnabled()
    expect(button.className).toMatch(/\bbg-primary\b/)
    expect(button.className).toMatch(/\btext-primary-foreground\b/)
    expect(button.className).not.toMatch(/\bdisabled:opacity-50\b/)
  })

  it('renders a disabled primary button with the unmistakable inert neutral', () => {
    render(<Button disabled>Save</Button>)

    const button = screen.getByRole('button', { name: 'Save' })
    expect(button).toBeDisabled()
    expect(button.className).toMatch(/\bdisabled:cursor-not-allowed\b/)
    expect(button.className).toMatch(/\bdisabled:bg-muted\b/)
    expect(button.className).toMatch(/\bdisabled:text-muted-foreground\b/)
    expect(button.className).not.toMatch(/\bdisabled:opacity-50\b/)
  })

  it('renders a disabled destructive button with the unmistakable inert neutral', () => {
    render(
      <Button variant="destructive" disabled>
        Delete
      </Button>,
    )

    const button = screen.getByRole('button', { name: 'Delete' })
    expect(button).toBeDisabled()
    expect(button.className).toMatch(/\bdisabled:cursor-not-allowed\b/)
    expect(button.className).toMatch(/\bdisabled:bg-muted\b/)
    expect(button.className).toMatch(/\bdisabled:text-muted-foreground\b/)
    expect(button.className).not.toMatch(/\bdisabled:opacity-50\b/)
  })

  it('renders a disabled outline button with the unmistakable inert neutral', () => {
    render(
      <Button variant="outline" disabled>
        Cancel
      </Button>,
    )

    const button = screen.getByRole('button', { name: 'Cancel' })
    expect(button).toBeDisabled()
    expect(button.className).toMatch(/\bdisabled:cursor-not-allowed\b/)
    expect(button.className).toMatch(/\bdisabled:bg-muted\b/)
    expect(button.className).toMatch(/\bdisabled:text-muted-foreground\b/)
    expect(button.className).not.toMatch(/\bdisabled:opacity-50\b/)
  })

  it('keeps the :disabled pseudo-class specificity above the variant background and foreground', () => {
    const classes = buttonVariants({ variant: 'default' }).split(/\s+/)
    const cursorIndex = classes.indexOf('disabled:cursor-not-allowed')
    const bgIndex = classes.indexOf('disabled:bg-muted')
    const textIndex = classes.indexOf('disabled:text-muted-foreground')

    expect(cursorIndex).toBeGreaterThanOrEqual(0)
    expect(bgIndex).toBeGreaterThanOrEqual(0)
    expect(textIndex).toBeGreaterThanOrEqual(0)
  })
})
