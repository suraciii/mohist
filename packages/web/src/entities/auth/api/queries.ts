import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { ApiError } from '../../../shared/api/client'
import { createSession, deleteSession, getSessionStatus } from './client'

export const authSessionQueryKey = ['auth', 'session'] as const

export function useAuthSession() {
  return useQuery({
    queryKey: authSessionQueryKey,
    queryFn: getSessionStatus,
    retry: false,
    staleTime: Infinity,
  })
}

export function useLogin() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: createSession,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: authSessionQueryKey })
    },
  })
}

export function useLogout() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: deleteSession,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: authSessionQueryKey })
    },
    onError: (error) => {
      if (error instanceof ApiError && error.status === 401) {
        queryClient.invalidateQueries({ queryKey: authSessionQueryKey })
        return
      }
      toast.error(error.message || 'Logout failed')
    },
  })
}
