import { createContext, useContext, useState, useCallback, type ReactNode } from 'react'
import type { Project } from '../lib/types'

interface ProjectContextValue {
  projectId: string | null
  setProjectId: (id: string) => void
  projects: Project[]
  setProjects: (projects: Project[]) => void
  currentProject: Project | null
}

const ProjectContext = createContext<ProjectContextValue | null>(null)

export function ProjectProvider({ children }: { children: ReactNode }) {
  const [projectId, setProjectIdState] = useState<string | null>(null)
  const [projects, setProjects] = useState<Project[]>([])

  const setProjectId = useCallback((id: string) => {
    setProjectIdState(id)
  }, [])

  const currentProject = projects.find((p) => p.id === projectId) ?? null

  return (
    <ProjectContext.Provider
      value={{ projectId, setProjectId, projects, setProjects, currentProject }}
    >
      {children}
    </ProjectContext.Provider>
  )
}

export function useProject() {
  const ctx = useContext(ProjectContext)
  if (!ctx) {
    throw new Error('useProject must be used within a ProjectProvider')
  }
  return ctx
}
