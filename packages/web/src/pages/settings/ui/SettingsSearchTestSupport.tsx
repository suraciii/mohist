import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, render } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation, useParams } from 'react-router-dom'
import type { Project } from '../../../entities/project'
import { ProjectProvider } from '../../../entities/project'
import {
  __resetShortcutHandlersForTesting,
  getShortcutHandler,
} from '../../../shared/lib/keyboard-shortcuts'
import { SettingsSearch } from './SettingsSearch'

const selectedProject: Project = {
  id: 'proj-selected',
  name: 'selected-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

export function makeQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
}

function TabPlaceholder() {
  const { section } = useParams<{ section: string }>()
  const ids = section === 'agent'
    ? ['agent-runtime-timeout']
    : section === 'ai'
      ? ['settings-default-model', 'settings-stage-model-plan']
      : section === 'repositories'
        ? ['repository-add-name']
        : section === 'templates'
          ? ['templates-search']
          : section === 'system'
            ? ['system-log-level']
            : section === 'preferences'
              ? ['preferences-theme']
              : ['workflow-profiles-section', 'project-default-workflow']
  return <div data-testid={`placeholder-section-${section}`}>{ids.map((id) => <input key={id} id={id} />)}</div>
}

function LocationSpy() {
  const location = useLocation()
  return <div data-testid="location-spy" data-pathname={location.pathname} />
}

export function renderSettingsSearch(initialEntry = '/settings/ai') {
  return render(
    <QueryClientProvider client={makeQueryClient()}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/settings/:section" element={<><TabPlaceholder /><SettingsSearch /></>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

export function renderSettingsSearchWithLocationSpy(initialEntry = '/settings/ai') {
  return render(
    <QueryClientProvider client={makeQueryClient()}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <LocationSpy />
        <Routes>
          <Route path="/settings/:section" element={<><TabPlaceholder /><SettingsSearch /></>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

export function renderSettingsSearchWithProject(initialEntry = '/settings/ai') {
  return render(
    <QueryClientProvider client={makeQueryClient()}>
      <ProjectProvider initialProjectId={selectedProject.id} initialProjects={[selectedProject]}>
        <MemoryRouter initialEntries={[initialEntry]}>
          <LocationSpy />
          <Routes>
            <Route path="/settings/:section" element={<><TabPlaceholder /><SettingsSearch /></>} />
            <Route path="/:projectName/settings/:section" element={<><TabPlaceholder /><SettingsSearch /></>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

export function openSettingsSearch() {
  const handler = getShortcutHandler('settings-search')
  if (!handler) throw new Error('SettingsSearch shortcut handler was not registered')
  act(() => handler())
}

export function resetSettingsSearchTestState() {
  cleanup()
  window.localStorage.clear()
  __resetShortcutHandlersForTesting()
}
