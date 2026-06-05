import { createContext, useContext, useState, useCallback, type ReactNode } from 'react'
import type { Project } from './types'

const SELECTED_PROJECT_STORAGE_KEY = 'mohist:selected-project-id'

interface ProjectContextValue {
  projectId: string | null
  setProjectId: (id: string | null) => void
  setProjectByName: (name: string | null) => void
  projects: Project[]
  setProjects: (projects: Project[]) => void
  currentProject: Project | null
}

const defaultProjectContext: ProjectContextValue = {
  projectId: null,
  setProjectId: () => {},
  setProjectByName: () => {},
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

  const setProjectByName = useCallback((name: string | null) => {
    if (!name) {
      setProjectId(null)
      return
    }

    const project = projects.find((p) => p.name === name) ?? null
    setProjectId(project?.id ?? null)
  }, [projects, setProjectId])

  const currentProject = projects.find((p) => p.id === projectId) ?? null

  return (
    <ProjectContext.Provider
      value={{ projectId, setProjectId, setProjectByName, projects, setProjects, currentProject }}
    >
      {children}
    </ProjectContext.Provider>
  )
}

export function projectPath(projectName: string | null | undefined, path: string = '') {
  const suffix = path === '/' ? '' : path.replace(/^\/+/, '')
  if (!projectName) return suffix ? `/${suffix}` : '/'
  return suffix ? `/${encodeURIComponent(projectName)}/${suffix}` : `/${encodeURIComponent(projectName)}`
}

export function useProjectPath() {
  const { currentProject } = useProject()
  return useCallback((path: string = '') => projectPath(currentProject?.name, path), [currentProject?.name])
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
