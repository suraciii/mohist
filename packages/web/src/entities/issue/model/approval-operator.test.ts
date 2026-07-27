import { describe, expect, it } from 'vitest'
import {
  APPROVAL_OPERATOR_MAX_LENGTH,
  isApprovalOperatorValid,
  normalizeApprovalOperator,
} from './approval-operator'

describe('approval operator', () => {
  it('normalizes surrounding whitespace and accepts one to 100 characters', () => {
    expect(normalizeApprovalOperator('  Ada  ')).toBe('Ada')
    expect(isApprovalOperatorValid(' Ada ')).toBe(true)
    expect(isApprovalOperatorValid(' ')).toBe(false)
    expect(isApprovalOperatorValid('a'.repeat(APPROVAL_OPERATOR_MAX_LENGTH))).toBe(true)
    expect(isApprovalOperatorValid('a'.repeat(APPROVAL_OPERATOR_MAX_LENGTH + 1))).toBe(false)
  })
})
