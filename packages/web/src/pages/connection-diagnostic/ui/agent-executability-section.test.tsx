import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import type { AgentExecutabilityFacts } from '../../../entities/agent-connection'
import { AgentExecutabilitySection, isAgentExecutionBlocked } from './agent-executability-section'

function makeExecutability(overrides: Partial<AgentExecutabilityFacts> = {}): AgentExecutabilityFacts {
  return {
    state: 'not-configured',
    gaps: [
      {
        code: 'runtime_missing',
        message: 'No Runtime is configured for this Agent.',
        nextAction: 'Choose a Runtime in Agent settings.',
        fixEntryPoint: { label: 'Agent settings', path: '/agents/agent-1', command: 'mo agent edit agent-1' },
      },
    ],
    pendingLaunchNote: null,
    ...overrides,
  }
}

function renderSection(executability: AgentExecutabilityFacts) {
  return render(<AgentExecutabilitySection executability={executability} />)
}

afterEach(cleanup)

describe('isAgentExecutionBlocked', () => {
  it('accepts only the states that stop the Agent from accepting work', () => {
    expect(isAgentExecutionBlocked('not-configured')).toBe(true)
    expect(isAgentExecutionBlocked('not-executable')).toBe(true)
    expect(isAgentExecutionBlocked('unknown')).toBe(false)
    expect(isAgentExecutionBlocked('executable')).toBe(false)
    expect(isAgentExecutionBlocked(null)).toBe(false)
  })
})

describe('AgentExecutabilitySection', () => {
  it('states the limitation separately from Connection setup', () => {
    renderSection(makeExecutability())

    const section = screen.getByTestId('connection-agent-executability')
    expect(section).toHaveTextContent('Slack setup for this Connection is complete.')
    expect(section).toHaveTextContent('No Runtime is configured for this Agent.')
    expect(section).toHaveTextContent('Choose a Runtime in Agent settings.')
  })

  it('repeats no repair link, because the projected action already carries it', () => {
    renderSection(makeExecutability())

    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it('renders every reported gap', () => {
    renderSection(
      makeExecutability({
        state: 'not-executable',
        gaps: [
          ...makeExecutability().gaps,
          {
            code: 'runner_unavailable',
            message: 'No Runner is available for this Agent.',
            nextAction: 'Check the Runner.',
            fixEntryPoint: { label: 'Runner settings', path: '/agents/agent-1', command: 'mo agent edit agent-1' },
          },
        ],
      }),
    )

    expect(screen.getByTestId('connection-agent-executability-gap-runtime_missing')).toBeInTheDocument()
    expect(screen.getByTestId('connection-agent-executability-gap-runner_unavailable')).toBeInTheDocument()
  })
})
