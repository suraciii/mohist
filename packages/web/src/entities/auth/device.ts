import { useMutation } from '@tanstack/react-query'
import { decideDevice, verifyDeviceCode } from './api/device'

export function useVerifyDeviceCode() {
  return useMutation({ mutationFn: verifyDeviceCode })
}

export function useDeviceDecision() {
  return useMutation({ mutationFn: ({ flowId, decision }: { flowId: string; decision: 'approved' | 'denied' }) =>
    decideDevice(flowId, decision),
  })
}
