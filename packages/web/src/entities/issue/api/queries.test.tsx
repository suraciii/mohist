import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook } from '@testing-library/react'
import { ProjectProvider } from '../../project'
import { useWorkflowTimeline } from './queries'

describe('useWorkflowTimeline', () => {
  it('does not configure a recurring refetch interval', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    renderHook(() => useWorkflowTimeline(14, false), {
      wrapper: ({ children }) => (
        <QueryClientProvider client={queryClient}>
          <ProjectProvider
            initialProjectId="project-1"
            initialProjects={[]}
          >
            {children}
          </ProjectProvider>
        </QueryClientProvider>
      ),
    })

    const query = queryClient.getQueryCache().find({
      queryKey: ['issue-workflow', 'project-1', 14, 'timeline'],
    })
    expect(query?.options.refetchInterval).toBe(false)
  })
})
