import { describe, it, expect, vi, beforeEach } from 'vitest'
import { act, render, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { toast } from 'sonner'
import { __testing__, type EventsConnectionHook } from '../src/app/providers/LiveTaskProvider'
import { dispatchRebaseEvent, onRebaseEvent } from '../src/entities/issue/model/rebase-events'
import { LiveTaskProvider } from '../src/app/providers/LiveTaskProvider'
import { RuntimeToastHost } from '../src/shared/ui/toast'
import { ProjectProvider } from '../src/entities/project'
import { useLiveTask } from '../src/entities/issue'
import { onAgentEvent } from '../src/entities/agent'
import { REVERSE_DNS_EVENT_TYPES } from '../src/shared/lib/canonical-event-types'
import { SUBSCRIPTION_EVENT_TYPES } from '../src/shared/api/events-hub'
import { useMswServer } from './support/msw'
import { issueCandidateKeys, issueDetailKeys, issueListKeys, issueWorkflowKeys } from '../src/entities/issue/api/query-keys'

useMswServer(
  http.get('*/api/projects/:projectId/agent/status', () =>
    HttpResponse.json({
      success: true,
      data: {
        running: false,
        activeAgents: [],
        runnerAvailable: true,
        capacity: { active: 0, max: 1 },
      },
    }),
  ),
)

const eventsConnectionHook = vi.fn<EventsConnectionHook>(() => ({
  status: 'connected',
  connection: null,
  reconnectVersion: 0,
}))

const { unwrapEnvelope, unwrapTranscriptEnvelope, routeTranscriptEventName } = __testing__

describe('unwrapEnvelope', () => {
  it('returns the data when given a CloudEvents 1.0.2 envelope', () => {
    const data = { issueNumber: 42, projectId: 'mohist' }
    const envelope = {
      type: 'stage_changed',
      data,
      id: 'evt-1',
      source: '/mohist/test',
      specVersion: '1.0',
    }
    expect(unwrapEnvelope(envelope)).toBe(data)
  })

  it('returns the payload when given the server CloudEventEnvelope shape', () => {
    const payload = { issueNumber: 42, projectId: 'mohist' }
    const envelope = {
      type: 'com.mohist.workflow.stage.started',
      payload,
      id: 'evt-1',
      source: '/mohist/test',
      specVersion: '1.0',
      dataContentType: 'application/json',
      extensions: { projectid: 'mohist' },
    }
    expect(unwrapEnvelope(envelope)).toBe(payload)
  })

  it('uses canonical routing extensions without mutating the display payload', () => {
    const payload = { projectId: 'payload-project', issueNumber: 42, healthStatus: 'yellow' }
    const envelope = {
      type: REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated,
      payload,
      id: 'evt-routing-context',
      source: '/mohist/agent-session/session-1',
      specVersion: '1.0',
      extensions: { projectid: 'canonical-project', issue: '99' },
    }

    expect(unwrapEnvelope(envelope)).toEqual({
      projectId: 'canonical-project',
      issueNumber: 99,
      healthStatus: 'yellow',
    })
    expect(payload).toEqual({ projectId: 'payload-project', issueNumber: 42, healthStatus: 'yellow' })
  })

  it('returns the raw object when given a back-compat raw payload', () => {
    const raw = { issueNumber: 42, projectId: 'mohist' }
    expect(unwrapEnvelope(raw)).toBe(raw)
  })

  it('returns empty record for null or undefined data', () => {
    expect(unwrapEnvelope(null)).toEqual({})
    expect(unwrapEnvelope(undefined)).toEqual({})
  })

  it('returns empty record when envelope data is non-object', () => {
    expect(unwrapEnvelope({
      type: 'x', data: 'string', id: 'a', source: 'b', specVersion: '1.0',
    })).toEqual({})
    expect(unwrapEnvelope({
      type: 'x', data: 42, id: 'a', source: 'b', specVersion: '1.0',
    })).toEqual({})
    expect(unwrapEnvelope({
      type: 'x', data: null, id: 'a', source: 'b', specVersion: '1.0',
    })).toEqual({})
  })

  it('extracts the nested payload for legacy back-compat shape', () => {
    // The old code path: any object with a 'payload' field returned the
    // payload. We still support that for unmigrated producers. The
    // structural check above covers the new CloudEvents path; the
    // legacy path here is documented as a back-compat fallback.
    const legacy = { type: 'tool_call', payload: { foo: 'bar' } }
    const result = unwrapEnvelope(legacy)
    expect(result).toEqual({ foo: 'bar' })
  })

  it('returns the envelope as-is when only the CloudEvents marker is partial', () => {
    // Malformed: missing 'source' — falls through to the legacy check
    // (which requires 'payload'), and since there's no payload, returns
    // the whole object. The point is: it does NOT silently treat the
    // partial envelope as a payload and drop fields.
    const partial = { type: 'x', id: 'a', data: { foo: 'bar' } }
    expect(unwrapEnvelope(partial)).toBe(partial)
  })

  it('returns the envelope as-is when missing type', () => {
    // Malformed: missing 'type' is the common bug class
    const noType = { id: 'a', source: 'b', specVersion: '1.0', data: { foo: 'bar' } }
    expect(unwrapEnvelope(noType)).toBe(noType)
  })

  it('returns the envelope as-is when missing required envelope fields', () => {
    // Malformed: missing 'source'
    const partial = { type: 'x', id: 'a', data: { foo: 'bar' } }
    expect(unwrapEnvelope(partial)).toBe(partial)
  })

  it('returns the envelope as-is when missing type', () => {
    // Malformed: missing 'type' is the common bug class
    const noType = { id: 'a', source: 'b', specVersion: '1.0', data: { foo: 'bar' } }
    expect(unwrapEnvelope(noType)).toBe(noType)
  })
})

function LiveTaskProbe() {
  const state = useLiveTask()
  return <div data-testid="active-task">{state.activeTaskId ?? ''}</div>
}

function rtlRender(ui: React.ReactElement) {
  return render(<RuntimeToastHost>{ui}</RuntimeToastHost>)
}

describe('LiveTaskProvider transcript routing', () => {
  beforeEach(() => {
    eventsConnectionHook.mockClear()
    vi.mocked(toast.info).mockClear()
  })

  it('unwraps transcript envelopes without dropping runtime row metadata', () => {
    const transcript = unwrapTranscriptEnvelope({
      Type: 'tool_call.started',
      SessionId: 'session-1',
      AgentSessionId: 'runtime-1',
      Sequence: 12,
      CreatedAt: '2026-06-11T00:00:00.0000000Z',
      WorkId: 'work-1',
      Payload: { toolCallId: 'tool-1', toolName: 'Read', status: 'started' },
    })

    expect(transcript).toMatchObject({
      eventName: 'tool_call.started',
      payload: { toolCallId: 'tool-1', toolName: 'Read', status: 'started' },
      detail: {
        Type: 'tool_call.started',
        SessionId: 'session-1',
        Sequence: 12,
        CreatedAt: '2026-06-11T00:00:00.0000000Z',
        WorkId: 'work-1',
        Payload: { toolCallId: 'tool-1', toolName: 'Read', status: 'started' },
        type: 'tool_call.started',
        toolCallId: 'tool-1',
        toolName: 'Read',
        state: 'started',
        status: 'started',
        runtimeSessionId: 'runtime-1',
        sessionId: 'session-1',
        payload: { toolCallId: 'tool-1', toolName: 'Read', status: 'started' },
      },
    })
  })

  it('routes OnTranscriptEvent envelopes through the live task handler', async () => {
    const queryClient = new QueryClient()
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const connectionCall = eventsConnectionHook.mock.calls[0]
    expect(connectionCall).toBeDefined()
    const onTranscriptEvent = connectionCall[2] as (envelope: unknown) => void

    onTranscriptEvent({
      Type: 'tool_call.started',
      SessionId: 'session-1',
      Sequence: 1,
      Payload: { toolCallId: 'tool-1', toolName: 'Read', status: 'started' },
    })

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    })
    expect(document.querySelector('[data-testid="active-task"]')?.textContent).toBe('')
  })

  it.each([
    ['message.delta', { text: 'hello' }],
    ['reasoning.delta', { text: 'thinking' }],
    ['tool_call.started', { toolName: 'Read', state: 'started', toolCallId: 'tool-1' }],
    ['session.input', { text: 'prompt', kind: 'task' }],
    ['session.closed', { status: 'completed' }],
  ] as const)('forwards %s transcript events to %s subscribers', async (eventName, partialPayload) => {
    const queryClient = new QueryClient()
    const received: unknown[] = []
    const off = onAgentEvent(routeTranscriptEventName(eventName) as Parameters<typeof onAgentEvent>[0], (detail) => received.push(detail))

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const connectionCall = eventsConnectionHook.mock.calls[0]
    const onTranscriptEvent = connectionCall[2] as (envelope: unknown) => void
    const payload = {
      projectId: 'project-1',
      executionId: 'execution-1',
      runtimeSessionId: 'runtime-1',
      ...partialPayload,
    }

    onTranscriptEvent({
      Type: eventName,
      SessionId: 'session-1',
      Sequence: 1,
      CreatedAt: '2026-06-11T00:00:00.0000000Z',
      Payload: payload,
    })

    await waitFor(() => {
      expect(received).toHaveLength(1)
      expect(received[0]).toMatchObject({
        Type: eventName,
        SessionId: 'session-1',
        Sequence: 1,
        CreatedAt: '2026-06-11T00:00:00.0000000Z',
        Payload: payload,
        type: eventName,
        payload,
        ...payload,
      })
    })
    off()
  })

  it.each([
    ['tool_call.updated', { toolName: 'Read', state: 'started', toolCallId: 'tool-1' }],
    ['tool_call.completed', { toolName: 'Read', state: 'completed', toolCallId: 'tool-1' }],
  ] as const)('routes %s transcript events to coder_tool_call subscribers', async (eventName, partialPayload) => {
    const queryClient = new QueryClient()
    const received: unknown[] = []
    const off = onAgentEvent(routeTranscriptEventName(eventName) as Parameters<typeof onAgentEvent>[0], (detail) => received.push(detail))

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const connectionCall = eventsConnectionHook.mock.calls[0]
    const onTranscriptEvent = connectionCall[2] as (envelope: unknown) => void
    const payload = {
      projectId: 'project-1',
      executionId: 'execution-1',
      runtimeSessionId: 'runtime-1',
      ...partialPayload,
    }

    onTranscriptEvent({
      Type: eventName,
      SessionId: 'session-1',
      Sequence: 1,
      CreatedAt: '2026-06-11T00:00:00.0000000Z',
      Payload: payload,
    })

    await waitFor(() => {
      expect(received).toHaveLength(1)
      expect(received[0]).toMatchObject({
        Type: eventName,
        SessionId: 'session-1',
        Sequence: 1,
        CreatedAt: '2026-06-11T00:00:00.0000000Z',
        Payload: payload,
        type: eventName,
        payload,
        ...payload,
      })
    })
    off()
  })

  it('shows approval toast for reverse-DNS approval-requested events', async () => {
    const queryClient = new QueryClient()
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const connectionCall = eventsConnectionHook.mock.calls[0]
    const onEvent = connectionCall[1] as (eventName: string, envelope: unknown) => void

    onEvent('com.mohist.workflow.stage.approval-requested', {
      id: 'evt-1',
      source: '/mohist/test',
      specVersion: '1.0',
      type: 'com.mohist.workflow.stage.approval-requested',
      payload: { projectId: 'project-1', issueNumber: 82, stage: 'review' },
    })

    await waitFor(() => {
      expect(toast.info).toHaveBeenCalledWith('Issue #82 needs approval')
    })
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: issueDetailKeys.detail('project-1', 82),
      exact: true,
    })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: issueListKeys.project('project-1') })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: issueWorkflowKeys.root('project-1', 82) })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
  })

  it('forwards canonical AgentSession context from a CloudEvent envelope', async () => {
    const queryClient = new QueryClient()
    const received: unknown[] = []
    const off = onAgentEvent(REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated, (detail) => received.push(detail))

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const onEvent = eventsConnectionHook.mock.calls[0][1] as (eventName: string, envelope: unknown) => void
    onEvent(REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated, {
      id: 'evt-health-1',
      source: '/mohist/agent-session/session-1',
      specVersion: '1.0',
      type: REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated,
      payload: { healthStatus: 'yellow', contextUsagePercent: 65 },
      extensions: { projectid: 'project-1', issue: '82' },
    })

    await waitFor(() => {
      expect(received).toEqual([{
        projectId: 'project-1',
        issueNumber: 82,
        healthStatus: 'yellow',
        contextUsagePercent: 65,
      }])
    })
    off()
  })

  it('subscribes to affiliation changes and invalidates Issue and Epic caches', async () => {
    const queryClient = new QueryClient()
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    expect(SUBSCRIPTION_EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueEpicChanged)

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const onEvent = eventsConnectionHook.mock.calls[0][1] as (eventName: string, envelope: unknown) => void
    onEvent(REVERSE_DNS_EVENT_TYPES.IssueEpicChanged, {
      id: 'evt-epic-change-1',
      source: '/mohist/projects/project-1/issues/82',
      specVersion: '1.0',
      type: REVERSE_DNS_EVENT_TYPES.IssueEpicChanged,
      payload: {},
      extensions: { projectid: 'project-1', issue: '82' },
    })

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({
        queryKey: issueDetailKeys.detail('project-1', 82),
        exact: true,
      })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: issueListKeys.project('project-1') })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['epics', 'project-1'] })
    })
  })

  it('shows merge completion toast for reverse-DNS completed events', async () => {
    const queryClient = new QueryClient()
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const connectionCall = eventsConnectionHook.mock.calls[0]
    const onEvent = connectionCall[1] as (eventName: string, envelope: unknown) => void

    onEvent('com.mohist.issue.completed', {
      id: 'evt-merge-1',
      source: '/mohist/test',
      specVersion: '1.0',
      type: 'com.mohist.issue.completed',
      payload: { projectId: 'project-1', issueNumber: 82, operation: 'merge' },
    })

    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Issue #82 merged successfully')
    })
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: issueDetailKeys.detail('project-1', 82),
      exact: true,
    })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: issueListKeys.project('project-1') })
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: issueCandidateKeys.project('project-1'),
      exact: true,
    })
  })

  it('shows merge failure toast for reverse-DNS workflow failed events', async () => {
    const queryClient = new QueryClient()

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const connectionCall = eventsConnectionHook.mock.calls[0]
    const onEvent = connectionCall[1] as (eventName: string, envelope: unknown) => void

    onEvent('com.mohist.workflow.run.failed', {
      id: 'evt-merge-failed-1',
      source: '/mohist/test',
      specVersion: '1.0',
      type: 'com.mohist.workflow.run.failed',
      payload: { projectId: 'project-1', issueNumber: 82, operation: 'merge', error: 'boom' },
    })

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith('Merge failed for Issue #82')
    })
  })

  it('dispatches rebase completion for reverse-DNS completed events', async () => {
    const queryClient = new QueryClient()
    const seen: unknown[] = []
    const off = onRebaseEvent((event) => seen.push(event))

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const connectionCall = eventsConnectionHook.mock.calls[0]
    const onEvent = connectionCall[1] as (eventName: string, envelope: unknown) => void

    onEvent('com.mohist.issue.completed', {
      id: 'evt-rebase-1',
      source: '/mohist/test',
      specVersion: '1.0',
      type: 'com.mohist.issue.completed',
      payload: { projectId: 'project-1', issueNumber: 82, operation: 'rebase', rebased: true },
    })

    await waitFor(() => {
       expect(seen).toContainEqual({ type: 'rebase_completed', issueNumber: 82, rebased: true })
    })
    off()
  })

  it('shows rebase conflict toast and updates conflict state for reverse-DNS failed events', async () => {
    const queryClient = new QueryClient()

    rtlRender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <RuntimeToastHost>
            <LiveTaskProvider eventsConnectionHook={eventsConnectionHook}>
              <LiveTaskProbe />
            </LiveTaskProvider>
          </RuntimeToastHost>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const connectionCall = eventsConnectionHook.mock.calls[0]
    const onEvent = connectionCall[1] as (eventName: string, envelope: unknown) => void

    act(() => {
      onEvent('com.mohist.workflow.stage.failed', {
        id: 'evt-rebase-conflict-1',
        source: '/mohist/test',
        specVersion: '1.0',
        type: 'com.mohist.workflow.stage.failed',
        payload: {
          projectId: 'project-1',
          issueNumber: 82,
          operation: 'rebase',
          conflicts: ['src/App.tsx'],
          error: 'conflict',
        },
      })
    })

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith('Rebase conflict on Issue #82')
    })
  })
})

describe('rebase events reach onRebaseEvent listeners', () => {
  beforeEach(() => {
    // The dispatch target is a module-level EventTarget. Listeners from
    // previous tests are not torn down here because dispatchRebaseEvent
    // is not in the test's import path; this is a focused test.
  })

  it('forwards rebase_started to a registered listener', () => {
    const seen: unknown[] = []
    const off = onRebaseEvent((e) => seen.push(e))
    // Drive the dispatch path the way LiveTaskProvider would
    const envelope = {
      type: 'rebase_started',
      data: { projectId: 'p1', issueNumber: 42 },
      id: 'evt-rb-1',
      source: '/mohist/test',
      specVersion: '1.0',
    }
    const payload = unwrapEnvelope(envelope) as { issueNumber: number }
    dispatchRebaseEvent({ type: 'rebase_started', issueNumber: payload.issueNumber })
    off()
    expect(seen).toEqual([{ type: 'rebase_started', issueNumber: 42 }])
  })
})

// silence: vi not used in this minimal set; left for future expansion
void vi
