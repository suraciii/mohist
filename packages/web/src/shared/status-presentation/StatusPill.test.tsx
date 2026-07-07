// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'

import { StatusPill } from './StatusPill'

afterEach(() => {
  cleanup()
})

describe('shared/status-presentation StatusPill', () => {
  it('renders children inside a Badge with the resolved treatment', () => {
    render(<StatusPill kind="issue-health" state="blocked">Blocked</StatusPill>)
    const pill = screen.getByText('Blocked')
    expect(pill).toBeInTheDocument()
    expect(pill.className).toContain('bg-danger-subtle')
    expect(pill.className).toContain('text-danger')
    expect(pill.className).toContain('border-danger-border')
    expect(pill.getAttribute('data-family')).toBe('danger')
    expect(pill.getAttribute('data-status')).toBe('blocked')
  })

  it('renders the muted treatment when given an unmapped state', () => {
    render(<StatusPill kind="workflow-run" state="some-new-state">Unknown</StatusPill>)
    const pill = screen.getByText('Unknown')
    expect(pill.className).toContain('bg-muted')
    expect(pill.className).toContain('text-muted-foreground')
    expect(pill.getAttribute('data-family')).toBe('muted')
  })

  it('renders the muted treatment when state is null', () => {
    render(<StatusPill kind="runner" state={null}>idle</StatusPill>)
    const pill = screen.getByText('idle')
    expect(pill.getAttribute('data-family')).toBe('muted')
    expect(pill.getAttribute('data-status')).toBe('unknown')
  })

  it('renders an optional dot whose class derives from the same family as the pill', () => {
    const { container } = render(
      <StatusPill kind="workflow-run" state="completed" withDot>
        Completed
      </StatusPill>,
    )
    const dot = container.querySelector('span[aria-hidden="true"]')
    expect(dot).not.toBeNull()
    expect(dot!.className).toContain('bg-success')
    expect(dot!.className).toContain('rounded-full')
  })

  it('does not render a dot by default', () => {
    const { container } = render(<StatusPill kind="runner" state="idle">Idle</StatusPill>)
    const dot = container.querySelector('span[aria-hidden="true"]')
    expect(dot).toBeNull()
  })

  it('renders an icon before children when provided', () => {
    render(
      <StatusPill kind="workflow-run" state="running" icon={<span data-testid="icon">*</span>}>
        Running
      </StatusPill>,
    )
    expect(screen.getByTestId('icon')).toBeInTheDocument()
    expect(screen.getByText('Running')).toBeInTheDocument()
  })

  it('forwards the testId to the rendered Badge', () => {
    render(
      <StatusPill kind="issue-health" state="done" testId="my-pill">
        Done
      </StatusPill>,
    )
    expect(screen.getByTestId('my-pill')).toBeInTheDocument()
  })

  it('appends arbitrary className without losing the treatment classes', () => {
    render(
      <StatusPill kind="approval" state="rejected" className="uppercase tracking-wide">
        Rejected
      </StatusPill>,
    )
    const pill = screen.getByText('Rejected')
    expect(pill.className).toContain('bg-danger-subtle')
    expect(pill.className).toContain('uppercase')
    expect(pill.className).toContain('tracking-wide')
  })
})