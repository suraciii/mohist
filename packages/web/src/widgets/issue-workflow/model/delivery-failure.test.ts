import { describe, expect, it } from 'vitest'
import { getDeliveryFailureGuidance, isDeliveryFailureKind } from './delivery-failure'

describe('delivery failure guidance', () => {
  it('maps a known structured error code to guidance', () => {
    expect(getDeliveryFailureGuidance('conflict')).toMatchObject({
      failureKind: 'conflict',
      retryable: false,
    })
  })

  it('does not classify message-like or unknown values', () => {
    expect(isDeliveryFailureKind('prepare failed (conflict)')).toBe(false)
    expect(getDeliveryFailureGuidance('prepare failed (conflict)')).toBeNull()
  })
})
