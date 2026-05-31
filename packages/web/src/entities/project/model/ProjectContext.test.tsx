// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { ProjectProvider, useProject } from './ProjectContext'

function ProjectProbe() {
  const { projectId, setProjectId } = useProject()

  return (
    <div>
      <div data-testid="project-id">{projectId ?? ''}</div>
      <button onClick={() => setProjectId('proj-2')}>Select project</button>
      <button onClick={() => setProjectId(null)}>Clear project</button>
    </div>
  )
}

describe('ProjectContext', () => {
  afterEach(() => {
    cleanup()
    window.localStorage.clear()
  })

  it('restores the selected project from local storage', () => {
    window.localStorage.setItem('mohist:selected-project-id', 'proj-1')

    render(
      <ProjectProvider>
        <ProjectProbe />
      </ProjectProvider>,
    )

    expect(screen.getByTestId('project-id')).toHaveTextContent('proj-1')
  })

  it('persists project selection changes', () => {
    render(
      <ProjectProvider>
        <ProjectProbe />
      </ProjectProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Select project' }))

    expect(window.localStorage.getItem('mohist:selected-project-id')).toBe('proj-2')
    expect(screen.getByTestId('project-id')).toHaveTextContent('proj-2')
  })

  it('clears persisted project selection when the selection is cleared', () => {
    window.localStorage.setItem('mohist:selected-project-id', 'proj-1')

    render(
      <ProjectProvider>
        <ProjectProbe />
      </ProjectProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Clear project' }))

    expect(window.localStorage.getItem('mohist:selected-project-id')).toBeNull()
    expect(screen.getByTestId('project-id')).toBeEmptyDOMElement()
  })
})
