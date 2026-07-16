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
    const { container: pendingContainer } = render(<><WorkflowRunStatusPill status="pending" /></>)
    const { container: readyContainer } = render(<><WorkflowRunStatusPill status="ready" /></>)
    const { container: runningContainer } = render(<><WorkflowRunStatusPill status="running" /></>)

    const pendingPill = pendingContainer.querySelector('[data-testid="workflow-run-status-pending"]') as HTMLElement
    const readyPill = readyContainer.querySelector('[data-testid="workflow-run-status-ready"]') as HTMLElement
    const runningPill = runningContainer.querySelector('[data-testid="workflow-run-status-running"]') as HTMLElement

    expect(pendingPill).not.toBeNull()
    expect(readyPill).not.toBeNull()
    expect(runningPill).not.toBeNull()

    const pendingClasses = pendingPill.className
    const readyClasses = readyPill.className
    const runningClasses = runningPill.className

    expect(pendingClasses).toContain('bg-violet-100')
    expect(readyClasses).toContain('bg-cyan-100')
    expect(runningClasses).toContain('bg-blue-100')

    expect(pendingClasses).toContain('text-violet-800')
    expect(readyClasses).toContain('text-cyan-800')
    expect(runningClasses).toContain('text-blue-800')

    expect(pendingClasses).not.toEqual(readyClasses)
    expect(readyClasses).not.toEqual(runningClasses)
    expect(pendingClasses).not.toEqual(runningClasses)
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

  it('renders completed as a distinct presentation', () => {
    renderPill('completed')

    const pill = screen.getByTestId('workflow-run-status-completed')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('completed')
    expect(pill).toHaveTextContent(/completed/i)
  })

  it('renders failed as a distinct presentation', () => {
    renderPill('failed')

    const pill = screen.getByTestId('workflow-run-status-failed')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('failed')
    expect(pill).toHaveTextContent(/failed/i)
  })

  it('renders stopped as a distinct presentation', () => {
    renderPill('stopped')

    const pill = screen.getByTestId('workflow-run-status-stopped')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('stopped')
    expect(pill).toHaveTextContent(/stopped/i)
  })

  it('falls back to an unknown presentation for an unrecognized status', () => {
    renderPill('mystery')

    const pill = screen.getByTestId('workflow-run-status-unknown')
    expect(pill).toBeInTheDocument()
    expect(pill.dataset.status).toBe('unknown')
    expect(pill).toHaveTextContent(/unknown/i)
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
