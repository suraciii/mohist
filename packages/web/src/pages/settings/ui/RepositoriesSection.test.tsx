// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { fireEvent } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { RepositoriesSection } from './RepositoriesSection'

const useRepositoriesMock = vi.fn()

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useRepositories: (projectId: string) => useRepositoriesMock(projectId),
    useAddRepository: () => ({ mutate: vi.fn(), isPending: false }),
    useRemoveRepository: () => ({ mutate: vi.fn(), isPending: false }),
    useSetDefaultRepository: () => ({ mutate: vi.fn(), isPending: false }),
  }
})

function renderSection() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <RepositoriesSection projectId="proj-test" />
    </QueryClientProvider>,
  )
}

describe('RepositoriesSection', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders the empty-state CTA without the add form', () => {
    useRepositoriesMock.mockReturnValue({ data: [], isLoading: false })

    renderSection()

    expect(screen.getByRole('button', { name: /Add your first repository/i })).toBeInTheDocument()
    expect(screen.queryByTestId('repository-add-form')).not.toBeInTheDocument()
  })

  it('focuses the Name input after clicking the empty-state CTA', () => {
    useRepositoriesMock.mockReturnValue({ data: [], isLoading: false })

    renderSection()
    fireEvent.click(screen.getByRole('button', { name: /Add your first repository/i }))

    expect(screen.getByTestId('repository-add-name')).toHaveFocus()
  })

  it('renders the add form when repositories exist', () => {
    useRepositoriesMock.mockReturnValue({
      data: [
        {
          name: 'main',
          gitUrl: 'git@example.com:main.git',
          baseBranch: 'main',
          isDefault: true,
        },
      ],
      isLoading: false,
    })

    renderSection()

    expect(screen.getByTestId('repository-add-form')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Add your first repository/i })).not.toBeInTheDocument()
  })
})
