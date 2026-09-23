import { describe, expect, it } from 'vitest'
import { getDeliveryFailureGuidance } from '../src/widgets/issue-workflow/model/delivery-failure'

describe('delivery failure protocol', () => {
  it('uses the action error code as the only classification input', () => {
    expect(getDeliveryFailureGuidance('base-moved')?.failureKind).toBe('base-moved')
    expect(getDeliveryFailureGuidance('Publish failed (base-moved)')).toBeNull()
  })
})
