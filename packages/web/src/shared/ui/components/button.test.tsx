import { describe, it, expect } from 'vitest'

import { buttonVariants } from './button'

describe('shared/ui Button variants', () => {
  it('success variant references success tokens, not raw green palette', () => {
    const className = buttonVariants({ variant: 'success' })
    expect(className).toContain('border-success-border')
    expect(className).toContain('bg-success-subtle')
    expect(className).toContain('text-success-foreground')
    expect(className).not.toContain('bg-green-')
    expect(className).not.toContain('text-green-')
    expect(className).not.toContain('border-green-')
  })

  it('info variant references info tokens, not raw blue palette', () => {
    const className = buttonVariants({ variant: 'info' })
    expect(className).toContain('border-info-border')
    expect(className).toContain('bg-info-subtle')
    expect(className).toContain('text-info-foreground')
    expect(className).not.toContain('bg-blue-')
    expect(className).not.toContain('text-blue-')
    expect(className).not.toContain('border-blue-')
  })

  it('warning variant references warning tokens, not raw amber palette', () => {
    const className = buttonVariants({ variant: 'warning' })
    expect(className).toContain('border-warning-border')
    expect(className).toContain('bg-warning-subtle')
    expect(className).toContain('text-warning-foreground')
    expect(className).not.toContain('bg-amber-')
    expect(className).not.toContain('text-amber-')
    expect(className).not.toContain('border-amber-')
  })

  it('danger variant references danger tokens, not raw red palette', () => {
    const className = buttonVariants({ variant: 'danger' })
    expect(className).toContain('border-danger-border')
    expect(className).toContain('bg-danger-subtle')
    expect(className).toContain('text-danger-foreground')
    expect(className).not.toContain('bg-red-')
    expect(className).not.toContain('text-red-')
    expect(className).not.toContain('border-red-')
  })

  it('destructive variant is an alias of the danger treatment', () => {
    const dangerClassName = buttonVariants({ variant: 'danger' })
    const destructiveClassName = buttonVariants({ variant: 'destructive' })
    expect(destructiveClassName).toBe(dangerClassName)
  })

  it('destructive variant references danger tokens, not raw red palette', () => {
    const className = buttonVariants({ variant: 'destructive' })
    expect(className).toContain('border-danger-border')
    expect(className).toContain('bg-danger-subtle')
    expect(className).toContain('text-danger-foreground')
    expect(className).not.toContain('bg-red-')
    expect(className).not.toContain('text-red-')
    expect(className).not.toContain('border-red-')
    expect(className).not.toContain('text-white')
  })

  it('default variant resolves to primary tokens', () => {
    const className = buttonVariants({ variant: 'default' })
    expect(className).toContain('bg-primary')
    expect(className).toContain('text-primary-foreground')
  })

  it('disabled treatment is uniform across variants', () => {
    const variants = ['default', 'outline', 'secondary', 'ghost', 'success', 'info', 'warning', 'danger', 'destructive', 'link'] as const
    for (const variant of variants) {
      const className = buttonVariants({ variant })
      expect(className).toContain('disabled:pointer-events-none')
      expect(className).toContain('disabled:opacity-50')
    }
  })

  it('semantic variants do not reference the legacy --destructive token classes', () => {
    const semanticVariants = ['success', 'info', 'warning', 'danger', 'destructive'] as const
    for (const variant of semanticVariants) {
      const className = buttonVariants({ variant })
      expect(className).not.toContain('bg-destructive/')
      expect(className).not.toContain('text-destructive')
    }
  })
})
