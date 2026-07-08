// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { getIssueWorkflowTaskLog } from '../../../entities/issue'
import { TaskLogPanel } from './TaskLogPanel'
import {
  buildHarness,
  fakeConnections,
  mockConnectionBuilder,
  makeLine,
  makePage,
  renderWithHarness,
} from './_taskLogPanelTestUtils'

vi.mock('@microsoft/signalr', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@microsoft/signalr')>()
  return {
    ...actual,
    HubConnectionBuilder: vi.fn(),
  }
})

vi.mock('../../../entities/issue/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue/api/client')>()
  return {
    ...actual,
    getIssueWorkflowTaskLog: vi.fn(),
  }
})

const mockedGetIssueWorkflowTaskLog = vi.mocked(getIssueWorkflowTaskLog)

describe('TaskLogPanel — action affordances use Button/Badge variants (D7)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    fakeConnections.length = 0
    mockConnectionBuilder()
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('download button uses the Button outline variant with no hand-rolled slate color overlay', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    const download = await screen.findByTestId('task-log-download-button')
    expect(download.dataset.slot).toBe('button')
    expect(download.className).toContain('border-border')
    expect(download.className).toContain('bg-background')
    expect(download.className).not.toContain('border-slate-')
    expect(download.className).not.toContain('bg-white')
    expect(download.className).not.toContain('text-slate-')
  })

  it('download button uses the primitive disabled treatment when the filtered set is empty', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    await screen.findByTestId('task-log-source-chips')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))

    const download = await screen.findByTestId('task-log-download-button')
    expect((download as HTMLButtonElement).disabled).toBe(true)
    expect(download.className).toContain('disabled:pointer-events-none')
    expect(download.className).toContain('disabled:opacity-50')
  })

  it('source chips render the Badge primitive with no hand-rolled slate color overlay', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
      makeLine({ seq: 2, source: 'cleanup', text: 'line-2' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const enabledChip = await screen.findByTestId('task-log-source-chip-workspace-prep')
    expect(enabledChip.dataset.slot).toBe('badge')
    expect(enabledChip.className).toContain('bg-secondary')
    expect(enabledChip.className).not.toContain('border-slate-')
    expect(enabledChip.className).not.toContain('bg-slate-')
    expect(enabledChip.className).not.toContain('text-slate-')
  })

  it('disabled source chips render the Badge outline variant with no hand-rolled slate color overlay', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))

    const disabledChip = await screen.findByTestId('task-log-source-chip-workspace-prep')
    expect(disabledChip.dataset.slot).toBe('badge')
    expect(disabledChip.className).toContain('border-border')
    expect(disabledChip.className).not.toContain('border-slate-')
    expect(disabledChip.className).not.toContain('bg-white')
    expect(disabledChip.className).not.toContain('text-slate-')
  })

  it('search input uses the standard --ring focus ring (no sky-500 override)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    const input = await screen.findByTestId('task-log-search-input')
    expect(input.className).toContain('focus-visible:border-ring')
    expect(input.className).toContain('focus-visible:ring-ring')
    expect(input.className).not.toContain('focus:border-sky-')
    expect(input.className).not.toContain('focus:ring-sky-')
  })

  it('panel chrome uses semantic tokens, not raw light-only palette classes', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    const panel = await screen.findByTestId('task-log-panel')
    const cls = panel.className
    expect(cls).toContain('border-border')
    expect(cls).toContain('bg-background')
    expect(cls).not.toContain('border-slate-')
    expect(cls).not.toContain('bg-white')
    expect(cls).not.toContain('text-slate-')
  })

  it('panel surface and truncation state avoid raw slate and amber palette classes', async () => {
    const harness = buildHarness(
      { lines: [makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' })], truncated: true, nextCursor: null },
      mockedGetIssueWorkflowTaskLog,
    )

    const { container } = renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-truncation-indicator')
    const html = container.innerHTML
    expect(html).toContain('text-muted-foreground')
    expect(html).toContain('bg-warning-subtle')
    expect(html).not.toContain('border-slate-')
    expect(html).not.toContain('text-slate-500')
    expect(html).not.toContain('bg-amber-')
    expect(html).not.toContain('text-amber-')
  })

  it('truncation indicator renders as a warning Badge, not a raw amber badge', async () => {
    const harness = buildHarness(
      { lines: [makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' })], truncated: true, nextCursor: null },
      mockedGetIssueWorkflowTaskLog,
    )

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    const indicator = await screen.findByTestId('task-log-truncation-indicator')
    expect(indicator.dataset.slot).toBe('badge')
    expect(indicator.className).toContain('border-warning-border')
    expect(indicator.className).toContain('bg-warning-subtle')
    expect(indicator.className).not.toContain('bg-amber-')
    expect(indicator.className).not.toContain('text-amber-')
  })
})
