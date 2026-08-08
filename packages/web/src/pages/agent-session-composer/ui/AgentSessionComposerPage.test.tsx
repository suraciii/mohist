import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { makeAgent, renderPage, state } from '../../../../tests/support/agent-session-composer-test-support'

describe('AgentSessionComposerPage', () => {
  beforeEach(() => {
    state.agentsData = []
    state.availabilityData = []
    state.launchCalls.length = 0
    state.launchError = null
    state.launchFailuresRemaining = -1
    state.launchResponse = null
  })

  afterEach(() => {
    cleanup()
  })

  /* ── Query-param parsing and pre-fill ─────────────────── */

  it('reads ?agent= to pre-select an agent', async () => {
    state.agentsData = [makeAgent('agent-1', { name: 'Agent One' })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    expect(await screen.findByTestId('agent-selector-trigger')).toHaveTextContent('Agent One')
  })

  it('reads ?issue= to pre-fill an issue context ref', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?issue=42'])
    expect(await screen.findByTestId('context-ref-chip-issue')).toHaveTextContent('Issue #42')
  })

  it('reads ?epic= to pre-fill an epic context ref', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?epic=7'])
    expect(await screen.findByTestId('context-ref-chip-epic')).toHaveTextContent('Epic: 7')
  })

  it('reads ?repo= to pre-fill a repo context ref', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?repo=org/repo'])
    expect(await screen.findByTestId('context-ref-chip-repository')).toHaveTextContent('Repository: org/repo')
  })

  it('reads ?ws= to pre-fill a workspace path context ref', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?ws=/home/project'])
    expect(await screen.findByTestId('context-ref-chip-workspace')).toHaveTextContent('Workspace: /home/project')
  })

  it('pre-fills multiple context refs simultaneously', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?issue=7&epic=3&repo=my/repo'])
    await screen.findByTestId('context-ref-chip-repository')
    expect(screen.getByTestId('context-ref-chip-issue')).toHaveTextContent('Issue #7')
    expect(screen.getByTestId('context-ref-chip-epic')).toHaveTextContent('Epic: 3')
    expect(screen.getByTestId('context-ref-chip-repository')).toHaveTextContent('Repository: my/repo')
  })

  /* ── Agent selection ──────────────────────────────────── */

  it('lists agents in the selector dropdown', async () => {
    state.agentsData = [makeAgent('agent-1'), makeAgent('agent-2')]
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-selector-trigger'))
    expect(screen.getByTestId('agent-option-agent-1')).toBeInTheDocument()
    expect(screen.getByTestId('agent-option-agent-2')).toBeInTheDocument()
  })

  it('selects agent from dropdown', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-selector-trigger'))
    fireEvent.click(screen.getByTestId('agent-option-agent-1'))
    expect(screen.getByTestId('agent-selector-trigger')).toHaveTextContent('Agent agent-1')
  })

  /* ── Prompt validation ────────────────────────────────── */

  it('disables launch when prompt is empty', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const button = await screen.findByTestId('launch-button')
    expect(button).toBeDisabled()
  })

  it('shows prompt error when textarea is blurred with empty value', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.focus(textarea)
    fireEvent.blur(textarea)
    expect(screen.getByTestId('prompt-error')).toBeInTheDocument()
    expect(screen.getByTestId('prompt-error')).toHaveTextContent('Prompt is required')
  })

  it('enables launch when prompt is filled and agent selected', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello agent' } })
    const button = screen.getByTestId('launch-button')
    expect(button).not.toBeDisabled()
  })

  /* ── Launch call + navigation ─────────────────────────── */

  it('calls mutate with correct args on launch', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello agent' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(state.launchCalls).toHaveLength(1)
      expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/sessions/sess-123')
    })
    expect(state.launchCalls[0]).toMatchObject({
      agentRef: 'agent-1',
      body: expect.objectContaining({ prompt: 'Hello agent' }),
    })
  })

  it('passes context refs in launch body', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1&issue=42&epic=7&repo=org/repo&ws=/workspace'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Analyze this' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(state.launchCalls).toHaveLength(1)
      expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/sessions/sess-123')
    })
    expect(state.launchCalls[0]).toMatchObject({
      agentRef: 'agent-1',
      body: {
        prompt: 'Analyze this',
        context: { issueNumber: 42, epicNumber: 7, repository: 'org/repo', workspace: '/workspace' },
        attachments: [],
      },
    })
    expect(state.launchCalls[0].body).not.toHaveProperty('runtime')
    expect(state.launchCalls[0].body).not.toHaveProperty('model')
    expect(state.launchCalls[0].body).not.toHaveProperty('variant')
    expect(state.launchCalls[0].body).not.toHaveProperty('skills')
    expect(state.launchCalls[0].body).not.toHaveProperty('maxConcurrentRuns')
  })

  it('sends attachment ids explicitly and displays mixed acceptance results', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchResponse = {
      attachments: [{ id: 'att-ok', name: 'accepted.txt', contentType: 'text/plain', size: 4 }],
      rejectedAttachments: [{ id: 'att-bad', reason: 'UnsupportedType', message: 'Archive files are not supported.' }],
    }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Use [accepted.txt](att:att-ok) and [rejected.zip](att:att-bad)' } })
    fireEvent.click(screen.getByTestId('launch-button'))

    await waitFor(() => expect(screen.getByTestId('launch-attachment-results')).toBeInTheDocument())
    expect(state.launchCalls[0].body).toMatchObject({ attachments: ['att-ok', 'att-bad'] })
    expect(screen.getByTestId('attachment-result-accepted-att-ok')).toHaveTextContent('accepted.txt')
    expect(screen.getByTestId('attachment-result-rejected-att-bad')).toHaveTextContent('Archive files are not supported.')
  })

  it('navigates to session detail on success', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/sessions/sess-123')
    })
  })

  it('uses the canonical session URL returned by launch', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchResponse = { sessionUrl: '/Test/sessions/canonical-1', sessionId: 'ignored-session' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Open directly' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/sessions/canonical-1'))
  })

  it('retains one idempotency key when the first response is lost and the launch is retried', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'response lost' }
    state.launchFailuresRemaining = 1
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Retry me' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => expect(state.launchCalls).toHaveLength(1))
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => expect(state.launchCalls).toHaveLength(2))

    expect(state.launchCalls[0].idempotencyKey).toBeTruthy()
    expect(state.launchCalls[1].idempotencyKey).toBe(state.launchCalls[0].idempotencyKey)
  })

  /* ── Context-ref chip remove ──────────────────────────── */

  it('removes context ref chip when X is clicked', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?issue=42'])
    expect(await screen.findByTestId('context-ref-chip-issue')).toBeInTheDocument()
    fireEvent.click(screen.getByTestId('remove-ref-issue'))
    expect(screen.queryByTestId('context-ref-chip-issue')).not.toBeInTheDocument()
  })

  /* ── Archived-agent launch disabling ──────────────────── */

  it('disables launch for archived agents', async () => {
    state.agentsData = [makeAgent('agent-1', { status: 'archived' })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    await screen.findByTestId('archived-warning')
    expect(screen.getByTestId('archived-warning')).toBeInTheDocument()
    const button = screen.getByTestId('launch-button')
    expect(button).toBeDisabled()
  })

  it('excludes archived agents from the launcher picker', async () => {
    state.agentsData = [
      makeAgent('agent-archived', { name: 'Archived One', status: 'archived' }),
      makeAgent('agent-active', { name: 'Active One', status: 'active' }),
    ]
    renderPage()
    fireEvent.click(await screen.findByTestId('agent-selector-trigger'))
    expect(screen.queryByTestId('agent-option-agent-archived')).not.toBeInTheDocument()
    expect(screen.getByTestId('agent-option-agent-active')).toBeInTheDocument()
  })

  it('shows the archived warning when ?agent= points at an archived agent even though it is not in the picker', async () => {
    state.agentsData = [makeAgent('agent-1', { status: 'archived' })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    await screen.findByTestId('archived-warning')
    expect(screen.getByTestId('archived-warning')).toBeInTheDocument()
    expect(screen.getByTestId('launch-button')).toBeDisabled()
  })

  /* ── Error states ─────────────────────────────────────── */

  it('surfaces no-available-runner error state', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'No available runner for selected agent', code: 'NO_AVAILABLE_RUNNER' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
    })
    expect(screen.getByTestId('error-no-runner')).toHaveTextContent(/no available runner/i)
  })

  it('surfaces external-agent-unavailable error state', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'External agent is unavailable', code: 'EXTERNAL_AGENT_UNAVAILABLE' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-external-agent')).toBeInTheDocument()
    })
    expect(screen.getByTestId('error-external-agent')).toHaveAttribute('data-feedback-kind', 'execution-unavailable')
    expect(screen.getByTestId('error-external-agent')).toHaveTextContent(/external agent/i)
    expect(screen.getByTestId('error-external-agent')).toHaveTextContent(/wait.*recover/i)
  })

  it('surfaces capacity back-pressure with a next action', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.availabilityData = [{
      agentId: 'agent-1',
      canStartNow: false,
      waitingReason: 'concurrency-limit',
      activeRuns: 1,
      maxConcurrentRuns: 1,
      capacity: { usedSlots: 1, totalSlots: 2 },
      queuedCount: 1,
    }]
    renderPage(['/agent-sessions/new?agent=agent-1'])

    const feedback = await screen.findByTestId('agent-availability-feedback')
    expect(feedback).toHaveAttribute('data-feedback-kind', 'back-pressure')
    expect(feedback).toHaveTextContent(/concurrency limit/i)
    expect(feedback).toHaveTextContent(/active run.*finish/i)
    fireEvent.change(screen.getByTestId('prompt-textarea'), { target: { value: 'Try later' } })
    expect(screen.getByTestId('launch-button')).not.toBeDisabled()
  })

  it('surfaces runtime execution unavailability with recovery guidance', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'runtime unavailable', code: 'runtime-unavailable' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Run this' } })
    fireEvent.click(screen.getByTestId('launch-button'))

    const feedback = await screen.findByTestId('error-execution-unavailable')
    expect(feedback).toHaveAttribute('data-feedback-kind', 'execution-unavailable')
    expect(feedback).toHaveTextContent(/execution backend unavailable/i)
    expect(feedback).toHaveTextContent(/recover/i)
  })

  it('matches no-runner error by message text fallback', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'No available runner for opencode' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
    })
  })

  it('prevents launch when error is present', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'No available runner' }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
    })
    fireEvent.change(screen.getByTestId('prompt-textarea'), { target: { value: 'Hello' } })
    expect(screen.getByTestId('error-no-runner')).toBeInTheDocument()
  })

  /* ── Readiness gating (server-conclusion driven, client does not synthesize) ── */

  it('blocks the launch button and lists gaps when Readiness is Needs setup', async () => {
    state.agentsData = [
      makeAgent('agent-1', {
        readiness: {
          conclusion: 'Needs setup',
          gaps: [
            { code: 'instructions-missing', message: 'Instructions are missing.', action: 'Add instructions in Agent settings.' },
          ],
          setup: { label: 'Agent settings', path: '/agents/agent-1/settings' },
        },
      }),
    ]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const banner = await screen.findByTestId('agent-readiness-needs-setup')
    expect(banner).toHaveTextContent(/needs setup/i)
    expect(screen.getByTestId('agent-readiness-gap-instructions-missing')).toHaveTextContent(/Instructions are missing/i)
    const button = screen.getByTestId('launch-button')
    expect(button).toBeDisabled()
  })

  it('marks the launch button as Ready when Readiness is Ready (no client synthesis)', async () => {
    state.agentsData = [
      makeAgent('agent-1', {
        readiness: { conclusion: 'Ready', gaps: [], setup: null },
      }),
    ]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    await screen.findByTestId('agent-readiness-ready')
    const textarea = screen.getByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    expect(screen.getByTestId('launch-button')).not.toBeDisabled()
  })

  it('keeps Unknown launchable and shows a will-wait-for-validation hint', async () => {
    state.agentsData = [makeAgent('agent-1', { readiness: { conclusion: 'Unknown', gaps: [], setup: null } })]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const hint = await screen.findByTestId('agent-readiness-unknown-hint')
    expect(hint).toHaveTextContent(/Readiness: Unknown/i)
    expect(hint).toHaveTextContent(/wait/i)
    const textarea = screen.getByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    expect(screen.getByTestId('launch-button')).not.toBeDisabled()
  })

  it('surfaces 409 agent_needs_setup gaps as an error banner', async () => {
    state.agentsData = [makeAgent('agent-1', {
      readiness: {
        conclusion: 'Unknown',
        gaps: [
          { code: 'instructions-missing', message: 'Instructions are missing.', action: 'Add instructions in Agent settings.' },
        ],
        setup: { label: 'Agent settings', path: '/agents/agent-1/settings' },
      },
    })]
    state.launchError = {
      error: 'This Agent needs setup before it can accept new work.',
      code: 'agent_needs_setup',
    }
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-needs-setup')).toBeInTheDocument()
    })
  })
})
