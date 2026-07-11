import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { SettingsSection } from './SettingsSection'

describe('SettingsSection', () => {
  it('renders the supplied page heading, description, and content', () => {
    render(
      <SettingsSection
        title="Runtime"
        description="Configure how Mohist schedules external coder agent sessions."
      >
        <button type="button">Save runtime settings</button>
      </SettingsSection>,
    )

    const section = screen.getByRole('heading', { name: 'Runtime', level: 2 }).closest('section')
    expect(section).not.toBeNull()
    expect(section).toHaveTextContent('Configure how Mohist schedules external coder agent sessions.')
    expect(section).toContainElement(screen.getByRole('button', { name: 'Save runtime settings' }))
  })

  it('does not render an empty description when none is supplied', () => {
    render(
      <SettingsSection title="Repositories">
        <div>Repository settings</div>
      </SettingsSection>,
    )

    const section = screen.getByRole('heading', { name: 'Repositories', level: 2 }).closest('section')
    expect(section?.querySelector('p')).toBeNull()
  })
})
