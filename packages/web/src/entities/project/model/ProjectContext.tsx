import { createContext, useContext, useState, useCallback, type ReactNode } from 'react'
import type { Project } from '../../../shared/api/types'

interface ProjectContextValue {
  projectId: string | null
  setProjectId: (id: string | null) => void
  projects: Project[]
  setProjects: (projects: Project[]) => void
  currentProject: Project | null
}

const defaultProjectContext: ProjectContextValue = {
  projectId: null,
  setProjectId: () => {},
  projects: [],
  setProjects: () => {},
  currentProject: null,
}

const ProjectContext = createContext<ProjectContextValue>(defaultProjectContext)

interface ProjectProviderProps {
  children: ReactNode
  initialProjectId?: string | null
  initialProjects?: Project[]
}

export function ProjectProvider({
  children,
  initialProjectId = null,
  initialProjects = [],
}: ProjectProviderProps) {
  const [projectId, setProjectIdState] = useState<string | null>(initialProjectId)
  const [projects, setProjects] = useState<Project[]>(initialProjects)

  const setProjectId = useCallback((id: string | null) => {
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
  return useContext(ProjectContext)
}
