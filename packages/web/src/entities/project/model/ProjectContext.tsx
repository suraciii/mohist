import { createContext, useContext, useState, useCallback, type ReactNode } from 'react'
import type { Project } from './types'

const SELECTED_PROJECT_STORAGE_KEY = 'mohist:selected-project-id'

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
  initialProjectId,
  initialProjects = [],
}: ProjectProviderProps) {
  const [projectId, setProjectIdState] = useState<string | null>(() =>
    initialProjectId !== undefined ? initialProjectId : readStoredProjectId(),
  )
  const [projects, setProjects] = useState<Project[]>(initialProjects)

  const setProjectId = useCallback((id: string | null) => {
    setProjectIdState(id)
    writeStoredProjectId(id)
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

function readStoredProjectId(): string | null {
  if (typeof window === 'undefined')
    return null

  try {
    return window.localStorage.getItem(SELECTED_PROJECT_STORAGE_KEY)
  } catch {
    return null
  }
}

function writeStoredProjectId(id: string | null) {
  if (typeof window === 'undefined')
    return

  try {
    if (id) {
      window.localStorage.setItem(SELECTED_PROJECT_STORAGE_KEY, id)
    } else {
      window.localStorage.removeItem(SELECTED_PROJECT_STORAGE_KEY)
    }
  } catch {
    // Persistence is a convenience; project selection should still work when storage is blocked.
  }
}
