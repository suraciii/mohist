import { describe, expect, it } from 'vitest'
import { CATEGORY_STYLES } from './types'

const SEMANTIC_FAMILIES = ['success', 'warning', 'info', 'danger', 'muted'] as const

function extractFamily(classSet: string): string | null {
  for (const family of SEMANTIC_FAMILIES) {
    if (classSet.includes(`bg-${family}-subtle`) || classSet.includes(`bg-${family}`)) {
      return family
    }
  }
  return null
}

describe('CATEGORY_STYLES maps every timeline category through the shared status layer', () => {
  it.each(Object.entries(CATEGORY_STYLES))(
    '%s category uses a semantic-token treatment, not a neutral gray fallback',
    (_category, style) => {
      const family = extractFamily(style.container)
      expect(family, `container for ${_category} must belong to a semantic family`).not.toBeNull()
      expect(SEMANTIC_FAMILIES).toContain(family)

      // No raw gray palette classes on the category surface.
      expect(style.container).not.toContain('bg-gray-')
      expect(style.dot).not.toContain('bg-gray-')
    },
  )

  it('success category resolves to the success family', () => {
    expect(CATEGORY_STYLES.success.container).toContain('bg-success-subtle')
    expect(CATEGORY_STYLES.success.container).toContain('text-success')
    expect(CATEGORY_STYLES.success.dot).toContain('bg-success')
  })

  it('workflow and integration categories resolve to the info family', () => {
    expect(CATEGORY_STYLES.workflow.container).toContain('bg-info-subtle')
    expect(CATEGORY_STYLES.integration.container).toContain('bg-info-subtle')
  })

  it('approval category resolves to the warning family', () => {
    expect(CATEGORY_STYLES.approval.container).toContain('bg-warning-subtle')
    expect(CATEGORY_STYLES.approval.container).toContain('text-warning')
  })

  it('failure category resolves to the danger family', () => {
    expect(CATEGORY_STYLES.failure.container).toContain('bg-danger-subtle')
    expect(CATEGORY_STYLES.failure.container).toContain('text-danger')
  })

  it('metadata category resolves to the muted family', () => {
    expect(CATEGORY_STYLES.metadata.container).toContain('bg-muted')
  })
})
