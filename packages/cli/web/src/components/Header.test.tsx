// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../context/ProjectContext'
import { Header } from './Header'

vi.mock('../hooks/useQueries', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../hooks/useQueries')>()
  return {
    ...actual,
    useDeleteProject: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
    useUseProject: () => ({ mutate: vi.fn() }),
  }
})

function renderHeader(initialRoute: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={[initialRoute]}>
          <Header onCreateIssue={vi.fn()} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('Header', () => {
  afterEach(() => {
    cleanup()
  })

  it('highlights Epics on epic detail routes', () => {
    renderHeader('/epic/epic-123')

    expect(screen.getByRole('button', { name: 'Epics' })).toHaveClass('bg-blue-50')
  })
})
