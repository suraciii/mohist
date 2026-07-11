import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { fireEvent } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import {
  RepositoriesSection,
  type RepositoriesSectionData,
} from './RepositoriesSection'

function renderSection(repositories: RepositoriesSectionData['repositories'] = []) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const dataHook = () => ({
    repositories,
    isLoading: false,
    addRepo: { mutate: () => {}, isPending: false },
    removeRepo: { mutate: () => {}, isPending: false },
    setDefault: { mutate: () => {} },
  }) as unknown as RepositoriesSectionData
  return render(
    <QueryClientProvider client={queryClient}>
      <RepositoriesSection projectId="proj-test" dataHook={dataHook} />
    </QueryClientProvider>,
  )
}

describe('RepositoriesSection', () => {
  afterEach(() => {
    cleanup()
  })

  it('renders the empty-state CTA without the add form', async () => {
    renderSection()

    expect(await screen.findByRole('button', { name: /Add your first repository/i })).toBeInTheDocument()
    expect(screen.queryByTestId('repository-add-form')).not.toBeInTheDocument()
  })

  it('focuses the Name input after clicking the empty-state CTA', async () => {
    renderSection()

    const cta = await screen.findByRole('button', { name: /Add your first repository/i })
    fireEvent.click(cta)

    expect(screen.getByTestId('repository-add-name')).toHaveFocus()
  })

  it('renders the add form when repositories exist', async () => {
    renderSection([
      {
        name: 'main',
        gitUrl: 'git@example.com:main.git',
        baseBranch: 'main',
        isDefault: true,
      },
    ])

    expect(await screen.findByTestId('repository-add-form')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Add your first repository/i })).not.toBeInTheDocument()
  })
})
