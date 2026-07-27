export const APPROVAL_OPERATOR_MAX_LENGTH = 100

export function normalizeApprovalOperator(value: string): string {
  return value.trim()
}

export function isApprovalOperatorValid(value: string): boolean {
  const normalized = normalizeApprovalOperator(value)
  return normalized.length > 0 && normalized.length <= APPROVAL_OPERATOR_MAX_LENGTH
}
