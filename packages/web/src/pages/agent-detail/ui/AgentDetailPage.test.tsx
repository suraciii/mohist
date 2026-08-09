import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, within } from '@testing-library/react'
import {
  makeAgent,
  makeSession,
  mockAgent,
  mockAgentError,
  mockSessions,
  renderJourneyPage,
  renderPage,
  resetState,
} from './AgentDetailPage.test-support'
import type { AgentInfo } from '../../../entities/agent'

describe('AgentDetailPage', () => {
  beforeEach(() => {
    resetState()
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('loading and error states', () => {
    it('shows loading state while agent is loading', () => {
      renderPage()
      expect(screen.getByText(/loading agent/i)).toBeInTheDocument()
    })

    it('shows error state when agent fetch fails', async () => {
      mockAgentError()
      renderPage()
      expect(await screen.findByText(/failed to load agent/i)).toBeInTheDocument()
    })
  })

  describe('profile summary', () => {
    it('renders the active Agent definition identity, instructions, and config', async () => {
      mockAgent(makeAgent())
      renderPage()
      await screen.findByTestId('agent-detail-page')
      expect(screen.getByText('Test Agent')).toBeInTheDocument()
      expect(screen.getByTestId('agent-detail-purpose')).toHaveTextContent('A test agent')
      expect(screen.getByTestId('agent-detail-lifecycle')).toHaveTextContent('Active')
      expect(screen.getByTestId('agent-detail-instructions')).toHaveTextContent('You are a helpful assistant.')
      expect(screen.getByTestId('agent-detail-config')).toBeInTheDocument()
      expect(screen.getByText('gpt-4')).toBeInTheDocument()
      expect(screen.getByText('high')).toBeInTheDocument()
    })

    it('renders URL and data avatars with accessible alternatives', async () => {
      mockAgent(makeAgent({ avatar: 'data:image/png;base64,AAAA' }))
      renderPage()

      const page = await screen.findByTestId('agent-detail-page')
      expect(within(page).getByRole('img', { name: 'Test Agent avatar' })).toHaveAttribute(
        'src',
        'data:image/png;base64,AAAA',
      )
      expect(screen.getByTestId('agent-detail-avatar')).toHaveClass('size-12', 'aspect-square')
    })

    it('falls back without changing the detail avatar frame when an image breaks', async () => {
      mockAgent(makeAgent({ avatar: 'https://example.test/broken-detail.png' }))
      renderPage()

      const page = await screen.findByTestId('agent-detail-page')
      fireEvent.error(within(page).getByRole('img', { name: 'Test Agent avatar' }))

      expect(screen.getByTestId('agent-detail-avatar')).toHaveAttribute('data-avatar-state', 'fallback')
      expect(screen.getByTestId('agent-detail-avatar')).toHaveClass('size-12', 'aspect-square')
    })

    it('renders the archived Agent definition identity and lifecycle', async () => {
      mockAgent(makeAgent({ description: 'Retained for audit', status: 'archived' }))
      renderPage()

      await screen.findByTestId('agent-detail-page')
      expect(screen.getByTestId('agent-detail-purpose')).toHaveTextContent('Retained for audit')
      expect(screen.getByTestId('agent-detail-lifecycle')).toHaveTextContent('Archived')
    })

    it('renders runtime, max concurrent runs, and edit timing in the definition summary', async () => {
      mockAgent(makeAgent({
        agentConfig: { runtime: 'pi', model: 'gpt-4', variant: 'high' },
        maxConcurrentRuns: 3,
      }))
      renderPage()

      await screen.findByTestId('agent-detail-page')
      expect(screen.getByTestId('agent-detail-runtime')).toHaveTextContent('Pi')
      expect(screen.getByTestId('agent-detail-max-concurrent-runs')).toHaveTextContent('3')
      expect(screen.getByTestId('agent-detail-edit-timing')).toHaveTextContent(/Jobs created after saving/i)
      expect(screen.getByTestId('agent-detail-edit-timing')).toHaveTextContent(/already in progress/i)
    })

    it('does not render an agent-type field (no "opencode" string anywhere on the surface)', async () => {
      mockAgent(
        makeAgent({
          agentConfig: {
            model: 'gpt-4',
            variant: 'high',
            type: 'opencode',
          } as AgentInfo['agentConfig'],
        }),
      )
      renderPage()
      const page = await screen.findByTestId('agent-detail-page')
      const pageText = page.textContent ?? ''
      expect(pageText).toMatch(/gpt-4/)
      expect(pageText).toMatch(/high/)
      expect(pageText).not.toMatch(/opencode/)
    })

    it('surfaces only model and variant in the Agent Config card when the persisted config carries legacy keys', async () => {
      mockAgent(
        makeAgent({
          agentConfig: {
            type: 'opencode',
            livenessQuietThresholdMs: 1200000,
            probeTimeoutMs: 30000,
            model: 'gpt-4',
            variant: 'high',
          } as AgentInfo['agentConfig'],
        }),
      )
      renderPage()
      const config = await screen.findByTestId('agent-detail-config')
      expect(config).toHaveTextContent('gpt-4')
      expect(config).toHaveTextContent('high')
      // Legacy keys are not surfaced in the Agent Config card at all.
      expect(config.textContent ?? '').not.toMatch(/opencode/)
      expect(config.textContent ?? '').not.toMatch(/liveness/i)
      expect(config.textContent ?? '').not.toMatch(/probe/i)
    })

    it('renders skills metadata', async () => {
      mockAgent(makeAgent())
      renderPage()
      await screen.findByTestId('agent-detail-skills')
      const skillsContainer = screen.getByTestId('agent-detail-skills')
      expect(skillsContainer).toBeInTheDocument()
      expect(skillsContainer).toHaveTextContent('code')
      expect(skillsContainer).toHaveTextContent('debug')
    })
  })

  describe('session history grouping', () => {
    it('renders sessions in running, failed, and ended sections', async () => {
      mockAgent(makeAgent())
      mockSessions([
        makeSession({ sessionId: 's1', activity: 'active' }),
        makeSession({ sessionId: 's2', activity: 'unknown' }),
        makeSession({ sessionId: 's3', activity: 'idle' }),
      ])
      renderPage()
      await screen.findByTestId('agent-detail-sessions')
      expect(screen.getByText('Running')).toBeInTheDocument()
      expect(screen.getByText('Failed')).toBeInTheDocument()
      expect(screen.getByText('Ended')).toBeInTheDocument()
    })

    it('shows empty sessions message when no sessions exist', async () => {
      mockAgent(makeAgent())
      renderPage()
      expect(await screen.findByText(/no sessions yet/i)).toBeInTheDocument()
    })
  })

  describe('new-session and edit entry points', () => {
    it('offers a new-session button for active profiles', async () => {
      mockAgent(makeAgent())
      renderPage()
      const newSessionBtn = await screen.findByTestId('agent-detail-new-session')
      expect(newSessionBtn).toBeInTheDocument()
      expect(newSessionBtn).not.toBeDisabled()
    })

    it('takes an active Agent from detail through the bound composer to its created Session', async () => {
      mockAgent(makeAgent({ name: 'Detail Agent' }))
      renderJourneyPage()

      fireEvent.click(await screen.findByTestId('agent-detail-new-session'))
      expect(await screen.findByTestId('agent-selector-trigger')).toHaveTextContent('Detail Agent')

      fireEvent.change(screen.getByTestId('launch-workspace'), { target: { value: 'workspace-1' } })
      fireEvent.change(screen.getByTestId('launch-repository'), { target: { value: 'main' } })
      fireEvent.change(screen.getByTestId('journey-prompt'), { target: { value: 'Check the launch path' } })
      fireEvent.click(screen.getByTestId('launch-button'))

      expect(await screen.findByTestId('created-session')).toBeInTheDocument()
    })

    it('disables new-session button for archived profiles', async () => {
      mockAgent(makeAgent({ status: 'archived' }))
      renderPage()
      const newSessionBtn = await screen.findByTestId('agent-detail-new-session')
      expect(newSessionBtn).toBeInTheDocument()
      expect(newSessionBtn).toBeDisabled()
    })

    it('shows edit button', async () => {
      mockAgent(makeAgent())
      renderPage()
      expect(await screen.findByTestId('agent-detail-edit')).toBeInTheDocument()
    })

    it('opens the profile editor when edit is clicked', async () => {
      mockAgent(makeAgent())
      renderPage()
      const editBtn = await screen.findByTestId('agent-detail-edit')
      fireEvent.click(editBtn)
      expect(screen.getByTestId('agent-profile-editor')).toBeInTheDocument()
    })
  })

  describe('Subscriptions section wiring', () => {
    it('mounts the SubscriptionsSection for an active agent with its own data-agent-id', async () => {
      mockAgent(makeAgent({ id: 'agent-42', status: 'active' }))
      renderPage()
      const section = await screen.findByTestId('agent-subscriptions-section')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('data-agent-id', 'agent-42')
      expect(section).toHaveAttribute('data-agent-status', 'active')
    })

    it('mounts the SubscriptionsSection for an archived agent and forwards the archived status', async () => {
      mockAgent(makeAgent({ status: 'archived' }))
      renderPage()
      const section = await screen.findByTestId('agent-subscriptions-section')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('data-agent-status', 'archived')
    })
  })

})
