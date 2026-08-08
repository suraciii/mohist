import { request } from '../../../shared/api/client'

export interface DeviceVerifyResult {
  flowId: string
  clientName: string | null
  expiresAt: string
}

export interface DeviceDecisionResult {
  status: 'approved' | 'denied'
}

/** Resolves a typed user code to its pending device authorization. */
export function verifyDeviceCode(userCode: string) {
  return request<DeviceVerifyResult>('/auth/device/verify', {
    method: 'POST',
    body: JSON.stringify({ userCode }),
  })
}

/** Records the confirmation-page decision for a pending authorization. */
export function decideDevice(flowId: string, decision: 'approved' | 'denied') {
  return request<DeviceDecisionResult>('/auth/device/decision', {
    method: 'POST',
    body: JSON.stringify({ flowId, decision }),
  })
}
