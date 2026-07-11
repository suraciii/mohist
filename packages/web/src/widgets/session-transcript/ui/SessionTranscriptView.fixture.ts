import { cleanup } from '@testing-library/react'
import { afterEach, beforeEach, vi } from 'vitest'
import { queryClients } from '../../../../tests/session-page-test-utils'
import { restoreScopedProperties, setScopedValue } from '../../../../tests/support/scoped-property'

export function installSessionTranscriptViewFixture(): void {
  beforeEach(() => {
    vi.clearAllMocks()
    setScopedValue(navigator, 'clipboard', { writeText: vi.fn().mockResolvedValue(undefined) })
    setScopedValue(Element.prototype, 'scrollTo', vi.fn())
  })

  afterEach(() => {
    cleanup()
    vi.useRealTimers()
    for (const queryClient of queryClients) queryClient.clear()
    queryClients.length = 0
    restoreScopedProperties()
  })
}
