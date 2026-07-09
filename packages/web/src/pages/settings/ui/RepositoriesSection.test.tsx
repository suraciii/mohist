// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { http, HttpResponse } from 'msw'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { fireEvent } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { RepositoriesSection } from './RepositoriesSection'
import { server, useMswServer } from '../../../../tests/support/msw'

const REPOSITORIES = '*/api/projects/:projectId/repositories'

let reposData: unknown[] = []

useMswServer(
  http.get(REPOSITORIES, () => HttpResponse.json({ success: true, data: reposData })),
)

function mockRepositories(data: unknown[]) {
  reposData = data
  server.use(http.get(REPOSITORIES, () => HttpResponse.json({ success: true, data })))
}

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
    mockRepositories([])
  })

  it('renders the empty-state CTA without the add form', async () => {
    mockRepositories([])

    renderSection()

    expect(await screen.findByRole('button', { name: /Add your first repository/i })).toBeInTheDocument()
    expect(screen.queryByTestId('repository-add-form')).not.toBeInTheDocument()
  })

  it('focuses the Name input after clicking the empty-state CTA', async () => {
    mockRepositories([])

    renderSection()

    const cta = await screen.findByRole('button', { name: /Add your first repository/i })
    fireEvent.click(cta)

    expect(screen.getByTestId('repository-add-name')).toHaveFocus()
  })

  it('renders the add form when repositories exist', async () => {
    mockRepositories([
      {
        name: 'main',
        gitUrl: 'git@example.com:main.git',
        baseBranch: 'main',
        isDefault: true,
      },
    ])

    renderSection()

    expect(await screen.findByTestId('repository-add-form')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Add your first repository/i })).not.toBeInTheDocument()
  })
})
