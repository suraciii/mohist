import React, { type ReactElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { render, type RenderOptions } from '@testing-library/react'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'

const TEST_PROJECT = {
  id: 'test-project',
  name: 'Test Project',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [{ name: 'main', gitUrl: 'git@example.com:test-project.git', baseBranch: 'main', isDefault: true }],
}

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
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
        <MemoryRouter>{children}</MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
}

interface CustomRenderOptions extends Omit<RenderOptions, 'wrapper'> {
  wrapper?: React.ComponentType<WrapperProps>
  /** MemoryRouter 初始路由（默认 "/"）。 */
  route?: string
  /** 覆盖 TEST_PROJECT 字段。 */
  project?: Partial<typeof TEST_PROJECT>
  /** 传入以便测试直接断言/操作 query cache；缺省每次渲染新建。 */
  queryClient?: QueryClient
}

function customRender(
  ui: ReactElement,
  {
    wrapper: WrapperComponent,
    route = '/',
    project,
    queryClient,
    ...options
  }: CustomRenderOptions = {}
) {
  if (WrapperComponent) {
    return render(ui, { wrapper: WrapperComponent, ...options })
  }

  const testProject = { ...TEST_PROJECT, ...project }
  const client = queryClient ?? createQueryClient()

  function DefaultWrapper({ children }: WrapperProps) {
    return (
      <QueryClientProvider client={client}>
        <ProjectProvider initialProjectId={testProject.id} initialProjects={[testProject]}>
          <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>
    )
  }

  return { ...render(ui, { wrapper: DefaultWrapper, ...options }), queryClient: client }
}

export * from '@testing-library/react'
export { render as baseRender, customRender as render, QueryClientWrapper, createQueryClient, TEST_PROJECT }
