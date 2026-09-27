export interface ApiResponse<T = unknown> {
  success: boolean
  data?: T
  error?: string
  code?: string
  details?: unknown
  effect?: string
  retrySafe?: boolean
  nextAction?: string
}
