// @vitest-environment jsdom
import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'

import { Badge } from './badge'
import { Button } from './button'

describe('Badge renders semantic variants', () => {
  it('renders success variant with the soft-tinted token classes', () => {
    render(<Badge variant="success" data-testid="badge">OK</Badge>)
    const el = screen.getByTestId('badge')
    expect(el.className).toContain('bg-success-subtle')
    expect(el.className).toContain('text-success')
    expect(el.className).toContain('border-success-border')
    expect(el.className).not.toContain('bg-emerald-')
  })

  it('renders info variant with the soft-tinted token classes', () => {
    render(<Badge variant="info" data-testid="badge">Info</Badge>)
    const el = screen.getByTestId('badge')
    expect(el.className).toContain('bg-info-subtle')
    expect(el.className).toContain('text-info')
    expect(el.className).toContain('border-info-border')
    expect(el.className).not.toContain('bg-blue-')
  })

  it('renders warning variant with the soft-tinted token classes', () => {
    render(<Badge variant="warning" data-testid="badge">Warn</Badge>)
    const el = screen.getByTestId('badge')
    expect(el.className).toContain('bg-warning-subtle')
    expect(el.className).toContain('text-warning')
    expect(el.className).toContain('border-warning-border')
    expect(el.className).not.toContain('bg-amber-')
  })

  it('renders danger variant with the soft-tinted token classes', () => {
    render(<Badge variant="danger" data-testid="badge">Oops</Badge>)
    const el = screen.getByTestId('badge')
    expect(el.className).toContain('bg-danger-subtle')
    expect(el.className).toContain('text-danger')
    expect(el.className).toContain('border-danger-border')
    expect(el.className).not.toContain('bg-red-')
  })

  it('renders destructive variant identically to danger (alias)', () => {
    const { rerender } = render(<Badge variant="destructive" data-testid="badge">X</Badge>)
    const destructiveClassName = screen.getByTestId('badge').className
    rerender(<Badge variant="danger" data-testid="badge">X</Badge>)
    const dangerClassName = screen.getByTestId('badge').className
    expect(destructiveClassName).toBe(dangerClassName)
  })
})

describe('Button renders semantic variants', () => {
  it('renders success variant with the token-backed classes', () => {
    render(<Button variant="success" data-testid="btn">OK</Button>)
    const el = screen.getByTestId('btn')
    expect(el.className).toContain('bg-success-subtle')
    expect(el.className).toContain('text-success')
    expect(el.className).toContain('border-success-border')
    expect(el.className).not.toContain('bg-green-')
  })

  it('renders warning variant with the token-backed classes', () => {
    render(<Button variant="warning" data-testid="btn">Warn</Button>)
    const el = screen.getByTestId('btn')
    expect(el.className).toContain('bg-warning-subtle')
    expect(el.className).toContain('text-warning')
    expect(el.className).toContain('border-warning-border')
    expect(el.className).not.toContain('bg-amber-')
  })

  it('renders danger variant with the token-backed classes', () => {
    render(<Button variant="danger" data-testid="btn">Oops</Button>)
    const el = screen.getByTestId('btn')
    expect(el.className).toContain('bg-danger-subtle')
    expect(el.className).toContain('text-danger')
    expect(el.className).toContain('border-danger-border')
    expect(el.className).not.toContain('bg-red-')
    expect(el.className).not.toContain('text-white')
  })

  it('renders destructive variant identically to danger (alias)', () => {
    const { rerender } = render(<Button variant="destructive" data-testid="btn">X</Button>)
    const destructiveClassName = screen.getByTestId('btn').className
    rerender(<Button variant="danger" data-testid="btn">X</Button>)
    const dangerClassName = screen.getByTestId('btn').className
    expect(destructiveClassName).toBe(dangerClassName)
  })

  it('applies the standard disabled treatment when disabled', () => {
    render(
      <Button variant="success" disabled data-testid="btn">
        X
      </Button>,
    )
    const el = screen.getByTestId('btn') as HTMLButtonElement
    expect(el.disabled).toBe(true)
    expect(el.className).toContain('disabled:pointer-events-none')
    expect(el.className).toContain('disabled:opacity-50')
  })
})
