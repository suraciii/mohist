import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { SETUP_STEPS, SetupStepList } from './setup-step-list'

describe('SetupStepList', () => {
  it('renders all setup steps in order', () => {
    render(<SetupStepList setupProgress="create_app_credentials" />)

    const list = screen.getByTestId('connection-setup-step-list')
    const renderedKeys = Array.from(list.querySelectorAll('li')).map((node) => node.getAttribute('data-testid'))
    expect(renderedKeys).toEqual(
      SETUP_STEPS.map((step) => `connection-setup-step-${step.key}`),
    )
  })

  it('marks only the current step as current and prior steps as done', () => {
    render(<SetupStepList setupProgress="claim_owner" />)

    for (const step of SETUP_STEPS) {
      const node = screen.getByTestId(`connection-setup-step-${step.key}`)
      const state =
        step.key === 'create_app_credentials' || step.key === 'waiting_for_slack_service' || step.key === 'fix_slack_setup'
          ? 'done'
          : step.key === 'claim_owner'
            ? 'current'
            : 'pending'
      expect(node).toHaveAttribute('data-state', state)
    }
  })

  it('treats complete as the final current step', () => {
    render(<SetupStepList setupProgress="complete" />)

    for (const step of SETUP_STEPS) {
      const node = screen.getByTestId(`connection-setup-step-${step.key}`)
      const expected =
        step.key === 'complete' ? 'current' : 'done'
      expect(node).toHaveAttribute('data-state', expected)
    }
  })

  it('treats unknown setup progress as the first step current', () => {
    render(<SetupStepList setupProgress={null} />)

    expect(screen.getByTestId('connection-setup-step-create_app_credentials')).toHaveAttribute('data-state', 'current')
    for (const step of SETUP_STEPS) {
      if (step.key === 'create_app_credentials') continue
      expect(screen.getByTestId(`connection-setup-step-${step.key}`)).toHaveAttribute('data-state', 'pending')
    }
  })
})
