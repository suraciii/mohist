import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { DelegationContentView } from './delegation-view'

describe('DelegationContentView', () => {
  it('renders description, subagentType, and childSessionId from details', () => {
    render(
      <DelegationContentView
        details={{
          description: 'Explore the codebase',
          subagentType: 'explorer',
          childSessionId: 'child-42',
        }}
      />,
    )

    expect(screen.getByText('Delegation')).toBeInTheDocument()
    expect(screen.getByText('explorer')).toBeInTheDocument()
    expect(screen.getByText('child-42')).toBeInTheDocument()
    expect(screen.getByText('Explore the codebase')).toBeInTheDocument()
  })

  it('falls back to input description when details omit it', () => {
    render(
      <DelegationContentView
        input={JSON.stringify({ description: 'fallback description' })}
        details={{ subagentType: 'explorer' }}
      />,
    )

    expect(screen.getByText('explorer')).toBeInTheDocument()
    expect(screen.getByText('fallback description')).toBeInTheDocument()
  })

  it('renders nothing when no relevant fields are present', () => {
    const { container } = render(
      <DelegationContentView
        input={JSON.stringify({ unrelated: true })}
        details={{}}
      />,
    )

    expect(container).toBeEmptyDOMElement()
  })
})
