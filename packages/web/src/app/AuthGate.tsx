import { useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { authSessionQueryKey, useAuthSession } from '../entities/auth'
import { setUnauthorizedListener } from '../shared/api/client'
import { LiveTaskProvider } from './providers/LiveTaskProvider'
import { AppContent } from './AppContent'
import { LoginPage } from '../pages/login'

export function AuthGate() {
  const queryClient = useQueryClient()
  const session = useAuthSession()

  useEffect(() => {
    setUnauthorizedListener(() => {
      queryClient.invalidateQueries({ queryKey: authSessionQueryKey })
    })
    return () => setUnauthorizedListener(null)
  }, [queryClient])

  if (session.isPending) {
    return (
      <div className="flex h-svh items-center justify-center text-sm text-muted-foreground">
        Loading…
      </div>
    )
  }

  if (session.isError) {
    return <LoginPage />
  }

  return (
    <LiveTaskProvider>
      <AppContent />
    </LiveTaskProvider>
  )
}
