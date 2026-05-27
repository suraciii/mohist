import React, { type ReactElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import { render, type RenderOptions } from '@testing-library/react'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'

const TEST_PROJECT = {
  id: 'test-project',
  name: 'Test Project',
  path: '/tmp/test-project',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
}

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
      mutations: {
        retry: false,
      },
    },
  })
}

interface WrapperProps {
  children: ReactNode
}

function QueryClientWrapper({ children }: WrapperProps) {
  const [queryClient] = React.useState(() => createQueryClient())

  return (
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <BrowserRouter>{children}</BrowserRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
}

interface CustomRenderOptions extends Omit<RenderOptions, 'wrapper'> {
  wrapper?: React.ComponentType<WrapperProps>
}

function customRender(
  ui: ReactElement,
  { wrapper: WrapperComponent = QueryClientWrapper, ...options }: CustomRenderOptions = {}
) {
  return render(ui, { wrapper: WrapperComponent, ...options })
}

export * from '@testing-library/react'
export { render as baseRender, customRender as render, QueryClientWrapper, createQueryClient, TEST_PROJECT }
