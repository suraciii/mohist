import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { WorkflowRunStatusPill } from './WorkflowRunStatusPill'

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

  it('uses visually distinct color treatments for pending, ready, and running', () => {
    const { container: pendingContainer } = render(<WorkflowRunStatusPill status="pending" />)
    const { container: readyContainer } = render(<WorkflowRunStatusPill status="ready" />)
    const { container: runningContainer } = render(<WorkflowRunStatusPill status="running" />)

    const pendingPill = pendingContainer.querySelector('[data-testid="workflow-run-status-pending"]') as HTMLElement
    const readyPill = readyContainer.querySelector('[data-testid="workflow-run-status-ready"]') as HTMLElement
    const runningPill = runningContainer.querySelector('[data-testid="workflow-run-status-running"]') as HTMLElement

    expect(pendingPill).not.toBeNull()
    expect(readyPill).not.toBeNull()
    expect(runningPill).not.toBeNull()
    expect(pendingPill.className).toContain('bg-violet-100')
    expect(readyPill.className).toContain('bg-cyan-100')
    expect(runningPill.className).toContain('bg-blue-100')
    expect(pendingPill.className).not.toEqual(readyPill.className)
    expect(readyPill.className).not.toEqual(runningPill.className)
    expect(pendingPill.className).not.toEqual(runningPill.className)
  })

  it('renders awaiting-approval as a distinct presentation', () => {
    renderPill('awaiting-approval')
    expect(screen.getByTestId('workflow-run-status-awaiting-approval')).toHaveTextContent(/awaiting approval/i)
  })

  it('renders paused as a distinct presentation', () => {
    renderPill('paused')
    expect(screen.getByTestId('workflow-run-status-paused')).toHaveTextContent(/paused/i)
  })

  it('renders created as a distinct presentation', () => {
    renderPill('created')
    expect(screen.getByTestId('workflow-run-status-created')).toHaveTextContent(/created/i)
  })

  it('renders completed as a distinct presentation', () => {
    renderPill('completed')
    expect(screen.getByTestId('workflow-run-status-completed')).toHaveTextContent(/completed/i)
  })

  it('renders failed as a distinct presentation', () => {
    renderPill('failed')
    expect(screen.getByTestId('workflow-run-status-failed')).toHaveTextContent(/failed/i)
  })

  it('renders stopped as a distinct presentation', () => {
    renderPill('stopped')
    expect(screen.getByTestId('workflow-run-status-stopped')).toHaveTextContent(/stopped/i)
  })

  it('falls back to an unknown presentation for an unrecognized status', () => {
    renderPill('mystery')
    const pill = screen.getByTestId('workflow-run-status-unknown')
    expect(pill.dataset.status).toBe('unknown')
    expect(pill).toHaveTextContent(/unknown/i)
  })

  it('renders nothing for a null status', () => {
    expect(renderPill(null).container).toBeEmptyDOMElement()
  })

  it('renders nothing for an undefined status', () => {
    expect(renderPill(undefined).container).toBeEmptyDOMElement()
  })
})
