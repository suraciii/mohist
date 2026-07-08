import { describe, it, expect } from 'vitest'

import { badgeVariants } from './badge'

describe('shared/ui Badge variants', () => {
  it('success variant references success tokens, not raw emerald palette', () => {
    const className = badgeVariants({ variant: 'success' })
    expect(className).toContain('border-success-border')
    expect(className).toContain('bg-success-subtle')
    expect(className).toContain('text-success')
    expect(className).not.toContain('text-success-foreground')
    expect(className).not.toContain('bg-emerald-')
    expect(className).not.toContain('text-emerald-')
    expect(className).not.toContain('border-emerald-')
  })

  it('info variant references info tokens, not raw blue palette', () => {
    const className = badgeVariants({ variant: 'info' })
    expect(className).toContain('border-info-border')
    expect(className).toContain('bg-info-subtle')
    expect(className).toContain('text-info')
    expect(className).not.toContain('text-info-foreground')
    expect(className).not.toContain('bg-blue-')
    expect(className).not.toContain('text-blue-')
    expect(className).not.toContain('border-blue-')
  })

  it('warning variant references warning tokens, not raw amber palette', () => {
    const className = badgeVariants({ variant: 'warning' })
    expect(className).toContain('border-warning-border')
    expect(className).toContain('bg-warning-subtle')
    expect(className).toContain('text-warning')
    expect(className).not.toContain('text-warning-foreground')
    expect(className).not.toContain('bg-amber-')
    expect(className).not.toContain('text-amber-')
    expect(className).not.toContain('border-amber-')
  })

  it('danger variant references danger tokens, not raw red palette', () => {
    const className = badgeVariants({ variant: 'danger' })
    expect(className).toContain('border-danger-border')
    expect(className).toContain('bg-danger-subtle')
    expect(className).toContain('text-danger')
    expect(className).not.toContain('text-danger-foreground')
    expect(className).not.toContain('bg-red-')
    expect(className).not.toContain('text-red-')
    expect(className).not.toContain('border-red-')
  })

  it('destructive variant is an alias of the danger treatment', () => {
    const dangerClassName = badgeVariants({ variant: 'danger' })
    const destructiveClassName = badgeVariants({ variant: 'destructive' })
    expect(destructiveClassName).toBe(dangerClassName)
  })

  it('destructive variant references danger tokens, not raw red palette', () => {
    const className = badgeVariants({ variant: 'destructive' })
    expect(className).toContain('border-danger-border')
    expect(className).toContain('bg-danger-subtle')
    expect(className).toContain('text-danger')
    expect(className).not.toContain('text-danger-foreground')
    expect(className).not.toContain('bg-red-')
    expect(className).not.toContain('text-red-')
    expect(className).not.toContain('border-red-')
  })

  it('default variant resolves to primary tokens', () => {
    const className = badgeVariants({ variant: 'default' })
    expect(className).toContain('bg-primary')
    expect(className).toContain('text-primary-foreground')
  })

  it('semantic variants do not reference the legacy --destructive token classes', () => {
    const semanticVariants = ['success', 'info', 'warning', 'danger', 'destructive'] as const
    for (const variant of semanticVariants) {
      const className = badgeVariants({ variant })
      expect(className).not.toContain('bg-destructive/')
      expect(className).not.toContain('text-destructive')
    }
  })
})
