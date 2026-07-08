// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { WorkflowRunStatusPill } from './WorkflowRunStatusPill'
import { familyFor, statusTreatment } from '@/shared/status-presentation'

afterEach(() => {
  cleanup()
})

function renderPill(status: string | null | undefined) {
  return render(<WorkflowRunStatusPill status={status} />)
}

describe('WorkflowRunStatusPill', () => {
  it('renders pending as a distinct presentation', () => {
    const { container } = renderPill('pending')

    const pill = screen.getByTestId('workflow-run-status-pending')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('pending')
    expect(pill).toHaveTextContent(/pending runner/i)

    const pendingPill = container.querySelector('[data-testid="workflow-run-status-pending"]')
    const readyPill = container.querySelector('[data-testid="workflow-run-status-ready"]')
    const runningPill = container.querySelector('[data-testid="workflow-run-status-running"]')
    expect(pendingPill).not.toBeNull()
    expect(readyPill).toBeNull()
    expect(runningPill).toBeNull()
  })

  it('renders ready as a distinct presentation from pending and running', () => {
    const { container } = renderPill('ready')

    const pill = screen.getByTestId('workflow-run-status-ready')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('ready')
    expect(pill).toHaveTextContent(/ready to run/i)

    const pendingPill = container.querySelector('[data-testid="workflow-run-status-pending"]')
    const readyPill = container.querySelector('[data-testid="workflow-run-status-ready"]')
    const runningPill = container.querySelector('[data-testid="workflow-run-status-running"]')
    expect(pendingPill).toBeNull()
    expect(readyPill).not.toBeNull()
    expect(runningPill).toBeNull()
  })

  it('renders running as a distinct presentation from pending and ready', () => {
    const { container } = renderPill('running')

    const pill = screen.getByTestId('workflow-run-status-running')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('running')
    expect(pill).toHaveTextContent(/running/i)

    const pendingPill = container.querySelector('[data-testid="workflow-run-status-pending"]')
    const readyPill = container.querySelector('[data-testid="workflow-run-status-ready"]')
    const runningPill = container.querySelector('[data-testid="workflow-run-status-running"]')
    expect(pendingPill).toBeNull()
    expect(readyPill).toBeNull()
    expect(runningPill).not.toBeNull()
  })

  it('uses the shared status-presentation layer for color tokens', () => {
    const { container: pendingContainer } = render(<WorkflowRunStatusPill status="pending" />)
    const { container: readyContainer } = render(<WorkflowRunStatusPill status="ready" />)
    const { container: runningContainer } = render(<WorkflowRunStatusPill status="running" />)

    const pendingPill = pendingContainer.querySelector('[data-testid="workflow-run-status-pending"]') as HTMLElement
    const readyPill = readyContainer.querySelector('[data-testid="workflow-run-status-ready"]') as HTMLElement
    const runningPill = runningContainer.querySelector('[data-testid="workflow-run-status-running"]') as HTMLElement

    const pendingTreatment = statusTreatment('workflow-run', 'pending')
    const readyTreatment = statusTreatment('workflow-run', 'ready')
    const runningTreatment = statusTreatment('workflow-run', 'running')

    expect(pendingPill.className).toContain(pendingTreatment.container.split(' ')[0]!)
    expect(readyPill.className).toContain(readyTreatment.container.split(' ')[0]!)
    expect(runningPill.className).toContain(runningTreatment.container.split(' ')[0]!)

    expect(pendingPill.dataset.family).toBe(pendingTreatment.family)
    expect(readyPill.dataset.family).toBe(readyTreatment.family)
    expect(runningPill.dataset.family).toBe(runningTreatment.family)

    expect(pendingTreatment.family).toBe('muted')
    expect(readyTreatment.family).toBe('info')
    expect(runningTreatment.family).toBe('info')

    // Pending (muted) differs from ready/running (info).
    expect(pendingPill.className).not.toEqual(readyPill.className)
    // `ready` and `running` share the `info` family by design (both
    // "in-progress, healthy" — see design D2). Their visual treatment
    // comes from the same `TREATMENT_BY_FAMILY` record, so they cannot
    // disagree on color. Labels (`Ready to run` vs `Running`) carry the
    // semantic distinction.
    expect(readyTreatment.family).toBe(runningTreatment.family)
  })

  it('completed renders with the success family (no emerald divergence)', () => {
    renderPill('completed')
    const pill = screen.getByTestId('workflow-run-status-completed')
    expect(pill.dataset.family).toBe('success')
    expect(familyFor('workflow-run', 'completed')).toBe('success')
    const cls = pill.className
    expect(cls).not.toMatch(/emerald|bg-green-|text-green-/)
    expect(cls).toMatch(/bg-success-subtle/)
  })

  it('failed renders with the danger family', () => {
    renderPill('failed')
    const pill = screen.getByTestId('workflow-run-status-failed')
    expect(pill.dataset.family).toBe('danger')
    const cls = pill.className
    expect(cls).not.toMatch(/bg-red-/)
    expect(cls).toMatch(/bg-danger-subtle/)
  })

  it('awaiting-approval renders with the warning family', () => {
    renderPill('awaiting-approval')
    const pill = screen.getByTestId('workflow-run-status-awaiting-approval')
    expect(pill.dataset.family).toBe('warning')
    const cls = pill.className
    expect(cls).not.toMatch(/bg-amber-/)
    expect(cls).toMatch(/bg-warning-subtle/)
  })

  it('renders awaiting-approval as a distinct presentation', () => {
    renderPill('awaiting-approval')

    const pill = screen.getByTestId('workflow-run-status-awaiting-approval')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('awaiting-approval')
    expect(pill).toHaveTextContent(/awaiting approval/i)
  })

  it('renders paused as a distinct presentation', () => {
    renderPill('paused')

    const pill = screen.getByTestId('workflow-run-status-paused')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('paused')
    expect(pill).toHaveTextContent(/paused/i)
  })

  it('renders created as a distinct presentation', () => {
    renderPill('created')

    const pill = screen.getByTestId('workflow-run-status-created')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('created')
    expect(pill).toHaveTextContent(/created/i)
  })

  it('renders stopped as a distinct presentation', () => {
    renderPill('stopped')

    const pill = screen.getByTestId('workflow-run-status-stopped')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('stopped')
    expect(pill).toHaveTextContent(/stopped/i)
  })

  it('drift renders with the warning family', () => {
    renderPill('drift')
    const pill = screen.getByTestId('workflow-run-status-drift')
    expect(pill.dataset.family).toBe('warning')
    expect(pill).toHaveTextContent(/drift/i)
    const cls = pill.className
    expect(cls).not.toMatch(/bg-amber-/)
    expect(cls).toMatch(/bg-warning-subtle/)
  })

  it('falls back to an unknown presentation for an unrecognized status', () => {
    renderPill('mystery')

    const pill = screen.getByTestId('workflow-run-status-unknown')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('unknown')
    expect(pill).toHaveTextContent(/unknown/i)
    expect(pill.dataset.family).toBe('muted')
  })

  it('renders nothing for a null status', () => {
    const { container } = renderPill(null)

    expect(container).toBeEmptyDOMElement()
  })

  it('renders nothing for an undefined status', () => {
    const { container } = renderPill(undefined)

    expect(container).toBeEmptyDOMElement()
  })
})