import '@testing-library/jest-dom'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { SidebarProvider } from '../../../shared/ui/components/sidebar'
import type { SystemInfo } from '../../../entities/settings'
import { SystemSettingsSection, type SystemSettingsDataHook } from './SystemSettingsSection'

const baseSystemInfo: SystemInfo = {
  running: {
    version: '0.1.0',
    gitHash: 'abcdef1234567890',
    startedAt: '2026-01-01T00:00:00Z',
  },
  source: {
    path: '/var/mohist/source',
    branch: 'master',
    head: 'abcdef1234567890',
    dirty: false,
  },
  install: {
    mode: 'binary',
    serviceManager: 'systemd',
    serverUnit: 'mohist-server.service',
    runnerUnit: 'mohist-runner.service',
    reason: null,
  },
  update: {
    status: 'up-to-date',
    available: false,
    reason: null,
  },
  services: {
    server: 'active',
    runner: 'active',
  },
  paths: {
    db: '/var/mohist/db.sqlite',
    config: '/etc/mohist/config.jsonc',
    opencode: '/var/mohist/opencode',
    logs: '/var/log/mohist',
  },
}

let systemInfoData: SystemInfo = baseSystemInfo
let systemInfoError = false

const dataHook: SystemSettingsDataHook = () => ({
  logLevelData: { level: 'INFO' },
  logLevelLoading: false,
  logLevelError: false,
  logLevelErrorValue: null,
  setLogLevel: async () => undefined,
  systemInfo: systemInfoError ? undefined : systemInfoData,
  infoLoading: false,
  infoError: systemInfoError,
  infoErrorValue: systemInfoError ? new Error('boom') : null,
  refetchInfo: async () => undefined,
  updateStatusEnvelope: { hasJob: false, job: null },
  refetchUpdateStatus: async () => undefined,
})

function mockSystemInfo(data: SystemInfo) {
  systemInfoData = data
  systemInfoError = false
}

function mockSystemInfoError() {
  systemInfoError = true
}

function renderSection() {
  return render(
    <MemoryRouter>
      <SidebarProvider>
        <SystemSettingsSection dataHook={dataHook} />
      </SidebarProvider>
    </MemoryRouter>,
  )
}

afterEach(() => {
  cleanup()
  mockSystemInfo(baseSystemInfo)
})

describe('SystemSettingsSection', () => {
  it('renders the Log Path sourced from systemInfo.paths.logs, not a hardcoded string', async () => {
    renderSection()

    const logPath = await screen.findByTestId('system-log-path')
    expect(logPath).toHaveTextContent('/var/log/mohist')
    expect(logPath).not.toHaveTextContent('~/.mohist/logs/')
  })

  it('falls back to em-dash when systemInfo.paths.logs is null', async () => {
    mockSystemInfo({ ...baseSystemInfo, paths: { ...baseSystemInfo.paths, logs: null } })

    renderSection()

    const logPath = await screen.findByTestId('system-log-path')
    expect(logPath).toHaveTextContent('—')
  })

  it('falls back to em-dash when systemInfo is unavailable', async () => {
    mockSystemInfoError()

    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Server Runtime')).toBeInTheDocument()
    })
    const logPath = screen.getByTestId('system-log-path')
    expect(logPath).toHaveTextContent('—')
  })

  it('keeps the Log Path value in sync with the Paths card logs entry', async () => {
    renderSection()

    const logPath = await screen.findByTestId('system-log-path')
    const pathsHeading = screen.getByRole('heading', { name: 'Paths', level: 3 })
    const pathsCard = pathsHeading.closest('section')
    expect(pathsCard).toBeTruthy()
    expect(pathsCard).toHaveTextContent('/var/log/mohist')
    expect(logPath).toHaveTextContent('/var/log/mohist')
  })

  it('relocates the edit-config guidance inside the Paths card as an inline amber note', async () => {
    renderSection()

    const note = await screen.findByTestId('system-edit-config-note')
    expect(note).toHaveTextContent(/config\.jsonc/i)
    const pathsHeading = screen.getByRole('heading', { name: 'Paths', level: 3 })
    const pathsCard = pathsHeading.closest('section')
    expect(pathsCard).toBeTruthy()
    expect(pathsCard).toContainElement(note)
  })

  it('does not render the edit-config guidance as a detached banner outside any card', async () => {
    renderSection()

    const note = await screen.findByTestId('system-edit-config-note')
    const containingCard = note.closest('section')
    expect(containingCard).toBeTruthy()
    expect(containingCard?.querySelector('h3')?.textContent).toBe('Paths')

    const settingsRoot = containingCard!.parentElement
    const directOrphans = Array.from(settingsRoot!.children).filter((child) => {
      if (child === containingCard) return false
      const text = child.textContent ?? ''
      return /Modify server-side config by editing config\.jsonc/.test(text)
    })
    expect(directOrphans).toHaveLength(0)
  })

  it('normalizes amber notes to a single shade token (text-amber-800) and forbids the legacy text-amber-700', async () => {
    renderSection()

    const note = await screen.findByTestId('system-edit-config-note')
    expect(note.className).toContain('text-amber-800')
    expect(note.className).not.toContain('text-amber-700')
  })
})
