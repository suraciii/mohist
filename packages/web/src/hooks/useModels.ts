import { useQuery } from '@tanstack/react-query'
import { api } from '../lib/api'

export function useModels() {
  return useQuery({
    queryKey: ['models'],
    queryFn: () => api.getAvailableModels(),
  })
}
