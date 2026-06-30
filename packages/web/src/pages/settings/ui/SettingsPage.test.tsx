// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Project } from '../../../entities/project'
import { ProjectProvider } from '../../../entities/project'
import { SidebarProvider } from '@/shared/ui/components/sidebar'
import { SettingsPage } from './SettingsPage'

const useRepositoriesMock = vi.fn()

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useRepositories: (projectId: string | undefined) => useRepositoriesMock(projectId),
    useAddRepository: () => ({ mutate: vi.fn(), isPending: false }),
    useRemoveRepository: () => ({ mutate: vi.fn(), isPending: false }),
    useSetDefaultRepository: () => ({ mutate: vi.fn(), isPending: false }),
  }
})

const projects: Project[] = [
  {
    id: 'proj-first',
    name: 'first-project',
    repositories: [
      { name: 'first', gitUrl: 'git@example.com:first.git', baseBranch: 'main', isDefault: true },
    ],
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
  },
  {
    id: 'proj-selected',
    name: 'selected-project',
    repositories: [
      { name: 'selected', gitUrl: 'git@example.com:selected.git', baseBranch: 'master', isDefault: true },
    ],
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
  },
]

const ONBOARDING_DISMISSED_KEY = 'mohist:settings-onboarding-dismissed'

function renderSettings(initialEntry = '/settings/repositories') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-selected" initialProjects={projects}>
        <SidebarProvider>
          <MemoryRouter initialEntries={[initialEntry]}>
            <Routes>
              <Route path="/settings/:section" element={<SettingsPage />} />
              <Route path="/:projectName/settings/:section" element={<SettingsPage />} />
            </Routes>
          </MemoryRouter>
        </SidebarProvider>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('SettingsPage', () => {
  afterEach(() => {
    cleanup()
    window.localStorage.clear()
    vi.clearAllMocks()
  })

  it('loads repositories for the selected project instead of the first project', () => {
    useRepositoriesMock.mockImplementation((projectId: string | undefined) => ({
      data: projectId === 'proj-selected' ? projects[1].repositories : projects[0].repositories,
      isLoading: false,
    }))

    renderSettings()

    expect(useRepositoriesMock).toHaveBeenCalledWith('proj-selected')
    expect(screen.getAllByText('selected').length).toBeGreaterThan(0)
    expect(screen.queryByText('first')).not.toBeInTheDocument()
  })

  it('shows onboarding banner on first visit to the Coder Agent tab', () => {
    renderSettings('/settings/ai')

    expect(screen.getByTestId('settings-onboarding-banner')).toHaveTextContent(
      'Start here — select the coder agent model used for workflow tasks',
    )
  })

  it('dismisses onboarding banner and persists across remounts', () => {
    const { unmount } = renderSettings('/settings/ai')

    fireEvent.click(screen.getByRole('button', { name: /dismiss onboarding banner/i }))

    expect(window.localStorage.getItem(ONBOARDING_DISMISSED_KEY)).toBe('true')
    expect(screen.queryByTestId('settings-onboarding-banner')).not.toBeInTheDocument()

    unmount()
    renderSettings('/settings/ai')

    expect(screen.queryByTestId('settings-onboarding-banner')).not.toBeInTheDocument()
  })

  it('shows onboarding banner again after localStorage is cleared', () => {
    const { unmount } = renderSettings('/settings/ai')

    fireEvent.click(screen.getByRole('button', { name: /dismiss onboarding banner/i }))
    unmount()
    window.localStorage.clear()
    renderSettings('/settings/ai')

    expect(screen.getByTestId('settings-onboarding-banner')).toBeInTheDocument()
  })

  it('does not show onboarding banner on non-ai tabs', () => {
    renderSettings('/settings/repositories')

    expect(screen.queryByTestId('settings-onboarding-banner')).not.toBeInTheDocument()
  })

  it('renders a navigable Preferences tab and resolves /settings/preferences', () => {
    renderSettings('/settings/preferences')

    expect(screen.getByTestId('settings-tab-preferences')).toBeInTheDocument()
    expect(screen.getByTestId('preferences-theme-card')).toBeInTheDocument()
    expect(screen.getByTestId('preferences-shortcuts-card')).toBeInTheDocument()
  })

  it('exposes the Preferences tab trigger alongside the other Settings tabs', () => {
    renderSettings('/settings/ai')

    for (const key of [
      'settings-tab-ai',
      'settings-tab-agent',
      'settings-tab-repositories',
      'settings-tab-workflows',
      'settings-tab-templates',
      'settings-tab-label-catalog',
      'settings-tab-inbox',
      'settings-tab-system',
      'settings-tab-preferences',
    ]) {
      expect(screen.getByTestId(key)).toBeInTheDocument()
    }
  })

  it('renders the Inbox tab and /settings/inbox resolves', () => {
    renderSettings('/settings/inbox')

    expect(screen.getByTestId('settings-tab-inbox')).toBeInTheDocument()
  })

  it('navigates to the Preferences tab from another Settings tab via the trigger', () => {
    renderSettings('/settings/ai')

    fireEvent.click(screen.getByTestId('settings-tab-preferences'))

    expect(screen.getByTestId('preferences-theme-card')).toBeInTheDocument()
    expect(screen.getByTestId('preferences-shortcuts-card')).toBeInTheDocument()
  })

  it('redirects an invalid section to the Coder Agent tab', () => {
    renderSettings('/settings/not-a-real-section')

    expect(screen.getByTestId('settings-onboarding-banner')).toBeInTheDocument()
  })
})
