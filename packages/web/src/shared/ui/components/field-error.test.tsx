import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'

import { FieldError } from './field-error'

describe('shared/ui FieldError', () => {
  it('renders the readable danger token text color, not hardcoded text-red-700', () => {
    render(<FieldError data-testid="error">something broke</FieldError>)
    const el = screen.getByTestId('error')
    expect(el.className).toContain('text-danger')
    expect(el.className).not.toContain('text-danger-foreground')
    expect(el.className).not.toContain('text-red-700')
  })

  it('preserves the alert role for assistive tech', () => {
    render(<FieldError data-testid="error">something broke</FieldError>)
    expect(screen.getByTestId('error')).toHaveAttribute('role', 'alert')
  })
})
