import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { makeAgent, makeWorkspace, renderPage, state } from '../../../../tests/support/agent-session-composer-test-support'
import { IssueHealth, IssueStatus } from '../../../entities/issue'
import { EpicStatus } from '../../../entities/epic'

describe('AgentSessionComposerPage', () => {
  beforeEach(() => {
    state.agentsData = []
    state.availabilityData = []
    state.launchCalls.length = 0
    state.launchError = null
    state.launchFailuresRemaining = -1
    state.launchResponse = null
    state.repositoriesData = []
    state.workspacesData = [makeWorkspace('workspace-1')]
    state.issuesData = []
    state.epicsData = []
  })

  afterEach(() => {
    cleanup()
  })

  function renderLaunchPage(initialEntries = ['/agent-sessions/new?agent=agent-1']) {
    return renderPage(initialEntries.map((entry) => `${entry}${entry.includes('?') ? '&' : '?'}workspace=workspace-1`))
  }
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

  it('does not treat a workspace path as confirmed launch scope', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1&ws=/home/project'])
    expect(await screen.findByTestId('workspace-scope-blocked')).toHaveTextContent(/active Workspace/i)
    expect(screen.getByTestId('launch-button')).toBeDisabled()
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

  it('keeps launch blocked until an explicit workspace is selected', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderPage(['/agent-sessions/new?agent=agent-1'])
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello agent' } })
    const button = screen.getByTestId('launch-button')
    expect(screen.getByTestId('workspace-scope-blocked')).toBeInTheDocument()
    expect(button).toBeDisabled()
    expect(state.launchCalls).toHaveLength(0)
  })

  /* ── Launch call + navigation ─────────────────────────── */

  it('calls mutate with correct args on launch', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderLaunchPage()
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
    state.workspacesData = [makeWorkspace('workspace-1', ['org/repo'])]
    renderPage(['/agent-sessions/new?agent=agent-1&issue=42&epic=7&repo=org/repo&workspace=workspace-1'])
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
        context: { issueNumber: 42, epicNumber: 7, repository: 'org/repo', workspace: 'workspace-1' },
        attachments: [],
      },
    })
    expect(state.launchCalls[0].body).not.toHaveProperty('runtime')
    expect(state.launchCalls[0].body).not.toHaveProperty('model')
    expect(state.launchCalls[0].body).not.toHaveProperty('variant')
    expect(state.launchCalls[0].body).not.toHaveProperty('skills')
    expect(state.launchCalls[0].body).not.toHaveProperty('maxConcurrentRuns')
  })

  it('constrains repositories to the selected workspace', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.repositoriesData = [
      { name: 'main', gitUrl: 'https://example.test/main.git', baseBranch: 'main', isDefault: true },
      { name: 'other', gitUrl: 'https://example.test/other.git', baseBranch: 'main', isDefault: false },
    ]
    state.workspacesData = [makeWorkspace('workspace-a', ['main'])]
    renderPage(['/agent-sessions/new?agent=agent-1&workspace=workspace-a&repo=other'])

    expect(await screen.findByTestId('repository-scope-blocked')).toBeInTheDocument()
    expect(screen.getByTestId('launch-repository')).not.toHaveTextContent('other')
    fireEvent.change(screen.getByTestId('prompt-textarea'), { target: { value: 'Use the selected scope' } })
    expect(screen.getByTestId('launch-button')).toBeDisabled()
    expect(state.launchCalls).toHaveLength(0)
  })

  it('lets the user select canonical task context and reviews its permission impact', async () => {
    state.agentsData = [makeAgent('agent-1', { name: 'Review Agent' })]
    state.repositoriesData = [{ name: 'web', gitUrl: 'https://example.test/web.git', baseBranch: 'main', isDefault: true }]
    state.workspacesData = [{
      projectId: 'proj-1',
      name: 'review-workspace',
      origin: { kind: 'manual' },
      repositories: ['web'],
      status: 'active',
      home: null,
      createdAt: '2026-06-01T00:00:00.000Z',
      boundSessionCount: 0,
    }]
    state.issuesData = [{
      number: 42,
      title: 'Review task',
      projectId: 'proj-1',
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      labels: {},
      createdAt: '2026-06-01T00:00:00.000Z',
      updatedAt: '2026-06-01T00:00:00.000Z',
      isDraft: false,
      canStart: true,
      blocker: null,
    }]
    state.epicsData = [{
      number: 7,
      title: 'Quality',
      description: '',
      projectId: 'proj-1',
      priority: 'p2',
      status: EpicStatus.Idle,
      createdAt: '2026-06-01T00:00:00.000Z',
      updatedAt: '2026-06-01T00:00:00.000Z',
      progress: { deliveredCount: 0, totalIssueCount: 0, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
    }]
    renderPage(['/agent-sessions/new?agent=agent-1'])

    fireEvent.change(screen.getByTestId('launch-workspace'), { target: { value: 'review-workspace' } })
    fireEvent.change(await screen.findByTestId('launch-repository'), { target: { value: 'web' } })
    fireEvent.change(screen.getByTestId('launch-issue'), { target: { value: '42' } })
    fireEvent.change(screen.getByTestId('launch-epic'), { target: { value: '7' } })

    expect(screen.getByTestId('scope-repository')).toHaveTextContent('web')
    expect(screen.getByTestId('scope-workspace')).toHaveTextContent('review-workspace')
    expect(screen.getByTestId('scope-issue')).toHaveTextContent('#42')
    expect(screen.getByTestId('scope-epic')).toHaveTextContent('#7')
    expect(screen.getByTestId('scope-permissions')).toHaveTextContent('review-workspace')

    fireEvent.change(screen.getByTestId('prompt-textarea'), { target: { value: 'Review the task' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => expect(state.launchCalls).toHaveLength(1))
    expect(state.launchCalls[0].body).toMatchObject({
      context: { repository: 'web', workspace: 'review-workspace', issueNumber: 42, epicNumber: 7 },
    })
  })

  it('sends attachment ids explicitly and displays mixed acceptance results', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchResponse = {
      attachments: [{ id: 'att-ok', name: 'accepted.txt', contentType: 'text/plain', size: 4 }],
      rejectedAttachments: [{ id: 'att-bad', reason: 'UnsupportedType', message: 'Archive files are not supported.' }],
      sessionUrl: '/Test/sessions/attachment-canonical-1',
    }
    renderLaunchPage()
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Use [accepted.txt](att:att-ok) and [rejected.zip](att:att-bad)' } })
    fireEvent.click(screen.getByTestId('launch-button'))

    await waitFor(() => expect(screen.getByTestId('launch-attachment-results')).toBeInTheDocument())
    expect(state.launchCalls[0].body).toMatchObject({ attachments: ['att-ok', 'att-bad'] })
    expect(screen.getByTestId('attachment-result-accepted-att-ok')).toHaveTextContent('accepted.txt')
    expect(screen.getByTestId('attachment-result-rejected-att-bad')).toHaveTextContent('Archive files are not supported.')

    fireEvent.click(screen.getByTestId('open-launched-session'))
    await waitFor(() => expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/sessions/attachment-canonical-1'))
    expect(screen.getByTestId('current-path')).not.toHaveTextContent('/Test/Test/sessions/')
  })

  it('navigates to session detail on success', async () => {
    state.agentsData = [makeAgent('agent-1')]
    renderLaunchPage()
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
    renderLaunchPage()
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Open directly' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/sessions/canonical-1'))
  })

  it('retains one idempotency key when the first response is lost and the launch is retried', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'response lost' }
    state.launchFailuresRemaining = 1
    renderLaunchPage()
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
    renderLaunchPage()
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
    renderLaunchPage()
    await screen.findByTestId('archived-warning')
    expect(screen.getByTestId('archived-warning')).toBeInTheDocument()
    expect(screen.getByTestId('launch-button')).toBeDisabled()
  })

  /* ── Error states ─────────────────────────────────────── */

  it('surfaces no-available-runner error state', async () => {
    state.agentsData = [makeAgent('agent-1')]
    state.launchError = { error: 'No available runner for selected agent', code: 'NO_AVAILABLE_RUNNER' }
    renderLaunchPage()
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
    renderLaunchPage()
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
    renderLaunchPage()

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
    renderLaunchPage()
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
    renderLaunchPage()
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
    renderLaunchPage()
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
    renderLaunchPage()
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
    renderLaunchPage()
    await screen.findByTestId('agent-readiness-ready')
    const textarea = screen.getByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    expect(screen.getByTestId('launch-button')).not.toBeDisabled()
  })

  it('keeps Unknown launchable and shows a will-wait-for-validation hint', async () => {
    state.agentsData = [makeAgent('agent-1', { readiness: { conclusion: 'Unknown', gaps: [], setup: null } })]
    renderLaunchPage()
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
    renderLaunchPage()
    const textarea = await screen.findByTestId('prompt-textarea')
    fireEvent.change(textarea, { target: { value: 'Hello' } })
    fireEvent.click(screen.getByTestId('launch-button'))
    await waitFor(() => {
      expect(screen.getByTestId('error-needs-setup')).toBeInTheDocument()
    })
  })
})
