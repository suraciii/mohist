import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { ConnectionDiagnostic } from '../../../entities/agent-connection'
import {
  ConnectionDiagnosticPage,
  type ConnectionDiagnosticPageDataHook,
  useReadOnlyOperations,
} from './ConnectionDiagnosticPage'

const diagnostic: ConnectionDiagnostic = {
  primaryState: 'owner_unavailable',
  reason: 'The current Slack Owner is no longer an eligible workspace member.',
  nextAction: 'Transfer ownership.',
  executability: {
    state: 'not-executable',
    gaps: [{
      code: 'execution-config-failure',
      message: 'The runtime rejected the configuration.',
      nextAction: 'Update the Agent execution settings.',
      fixEntryPoint: {
        label: 'Agent settings',
        path: '/agents/agent-1',
        command: 'mo agent edit agent-1',
      },
    }],
    pendingLaunchNote: 'A retry will verify the updated definition.',
  },
  facts: {
    setupProgress: 'complete',
    desiredState: 'enabled',
    connectionHealth: 'healthy',
    healthReason: null,
    credentialStatus: 'valid',
    adapterOnline: true,
    ownerAvailability: 'unavailable',
    agentReadiness: 'ready',
    identity: {
      verificationStatus: 'verified',
      verifiedBotName: 'Slack Bot',
      botName: 'Configured Bot',
      agentName: 'Writer',
      verifiedBotIconUrl: 'https://slack.example/icon.png',
      avatarHash: 'avatar-hash',
      driftKinds: ['presentation_name', 'avatar'],
    },
    offlineGapAt: null,
  },
}

function renderPage(dataHook: ConnectionDiagnosticPageDataHook) {
  return render(
    <MemoryRouter initialEntries={['/test/connections/connection-1']}>
      <Routes>
        <Route
          path="/:projectName/connections/:connectionId"
          element={<ConnectionDiagnosticPage dataHook={dataHook} operationsHook={useReadOnlyOperations} />}
        />
      </Routes>
    </MemoryRouter>,
  )
}

afterEach(cleanup)

describe('ConnectionDiagnosticPage', () => {
  it('renders the one next action and expandable independent facts', () => {
    renderPage(() => ({ data: diagnostic, isLoading: false, error: null }))

    expect(screen.getByTestId('connection-diagnostic-primary-state')).toHaveTextContent('owner unavailable')
    expect(screen.getByTestId('connection-diagnostic-reason')).toHaveTextContent(diagnostic.reason)
    expect(screen.getByTestId('connection-diagnostic-next-action')).toHaveTextContent('Transfer ownership.')

    fireEvent.click(screen.getByText('Supporting facts'))
    const facts = screen.getByTestId('connection-diagnostic-facts')
    expect(facts).toHaveAttribute('open')
    expect(facts).toHaveTextContent('Owner')
    expect(facts).toHaveTextContent('unavailable')
    expect(facts).toHaveTextContent('Slack Bot')
    expect(facts).toHaveTextContent('https://slack.example/icon.png')
    expect(facts).toHaveTextContent('presentation name, avatar')

    fireEvent.click(screen.getByText('Agent executability'))
    const executability = screen.getByTestId('connection-diagnostic-executability')
    expect(executability).toHaveAttribute('open')
    expect(executability).toHaveTextContent('not executable')
    expect(executability).toHaveTextContent('execution-config-failure')
    expect(executability).toHaveTextContent('The runtime rejected the configuration.')
    expect(executability).toHaveTextContent('Update the Agent execution settings.')
    expect(executability).toHaveTextContent('/agents/agent-1')
    expect(executability).toHaveTextContent('mo agent edit agent-1')
  })

  it('renders loading and error states', () => {
    const { rerender } = renderPage(() => ({ data: undefined, isLoading: true, error: null }))
    expect(screen.getByText('Loading connection...')).toBeInTheDocument()

    rerender(
      <MemoryRouter initialEntries={['/test/connections/connection-1']}>
        <Routes>
          <Route
            path="/:projectName/connections/:connectionId"
            element={
              <ConnectionDiagnosticPage
                dataHook={() => ({ data: undefined, isLoading: false, error: new Error('Request failed') })}
                operationsHook={useReadOnlyOperations}
              />
            }
          />
        </Routes>
      </MemoryRouter>,
    )
    expect(screen.getByTestId('connection-diagnostic-error')).toHaveTextContent('Request failed')
  })
})
