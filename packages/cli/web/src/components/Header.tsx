import { useState, useRef, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useProject } from '../context/ProjectContext'
import type { Project } from '../lib/types'
import { CreateIssueDialog } from './CreateIssueDialog'
import { CreateProjectDialog } from './CreateProjectDialog'
import { Dialog } from './Dialog'
import { useDeleteProject, useUseProject } from '../hooks/useQueries'

export function Header() {
  const { projectId, setProjectId, projects, currentProject } = useProject()
  const navigate = useNavigate()
  const [dropdownOpen, setDropdownOpen] = useState(false)
  const [createIssueOpen, setCreateIssueOpen] = useState(false)
  const [createProjectOpen, setCreateProjectOpen] = useState(false)
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false)
  const dropdownRef = useRef<HTMLDivElement>(null)

  const deleteProject = useDeleteProject()
  const switchProject = useUseProject()

  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (dropdownRef.current && !dropdownRef.current.contains(e.target as Node)) {
        setDropdownOpen(false)
      }
    }
    if (dropdownOpen) {
      document.addEventListener('mousedown', handleClickOutside)
      return () => document.removeEventListener('mousedown', handleClickOutside)
    }
  }, [dropdownOpen])

  const handleSelect = (project: Project) => {
    setProjectId(project.id)
    setDropdownOpen(false)
  }

  function handleDelete() {
    if (!currentProject) return
    deleteProject.mutate(currentProject.name, {
      onSuccess: () => {
        setDeleteConfirmOpen(false)
        setDropdownOpen(false)
        const remaining = projects.filter((p) => p.id !== currentProject.id)
        if (remaining.length > 0) {
          setProjectId(remaining[0].id)
          switchProject.mutate(remaining[0].name)
        } else {
          setProjectId(null)
        }
      },
    })
  }

  return (
    <>
      <header className="h-14 border-b border-gray-200 bg-white flex items-center px-6 shrink-0">
        <h1 className="text-lg font-bold text-gray-900 tracking-tight">mohist</h1>

        <div className="ml-6" ref={dropdownRef}>
          {projects.length === 0 ? (
            <span className="text-sm text-gray-600 font-medium">
              No projects
            </span>
          ) : projects.length === 1 ? (
            <span className="text-sm text-gray-600 font-medium">
              {currentProject?.name ?? 'Loading...'}
            </span>
          ) : (
            <div className="relative">
              <button
                onClick={() => setDropdownOpen(!dropdownOpen)}
                className="flex items-center gap-1.5 text-sm text-gray-700 hover:text-gray-900 font-medium px-2.5 py-1.5 rounded-md hover:bg-gray-100 transition-colors"
              >
                <span>{currentProject?.name ?? 'Select project'}</span>
                <svg
                  className={`h-4 w-4 text-gray-400 transition-transform ${dropdownOpen ? 'rotate-180' : ''}`}
                  viewBox="0 0 20 20"
                  fill="currentColor"
                >
                  <path
                    fillRule="evenodd"
                    d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
                    clipRule="evenodd"
                  />
                </svg>
              </button>

              {dropdownOpen && (
                <div className="absolute top-full left-0 mt-1 w-56 rounded-md border border-gray-200 bg-white shadow-lg py-1 z-50">
                  {projects.map((project) => (
                    <button
                      key={project.id}
                      onClick={() => handleSelect(project)}
                      className={`w-full text-left px-3 py-2 text-sm hover:bg-gray-50 transition-colors ${
                        project.id === projectId
                          ? 'text-blue-600 bg-blue-50 font-medium'
                          : 'text-gray-700'
                      }`}
                    >
                      <div className="font-medium">{project.name}</div>
                      <div className="text-xs text-gray-400 truncate mt-0.5">{project.path}</div>
                    </button>
                  ))}

                  <div className="border-t border-gray-100 my-1" />

                  <button
                    onClick={() => {
                      setDropdownOpen(false)
                      setCreateProjectOpen(true)
                    }}
                    className="w-full text-left px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors"
                  >
                    New Project
                  </button>

                  <button
                    onClick={() => {
                      setDropdownOpen(false)
                      setDeleteConfirmOpen(true)
                    }}
                    className="w-full text-left px-3 py-2 text-sm text-red-600 hover:bg-red-50 transition-colors"
                  >
                    Delete Project
                  </button>
                </div>
              )}
            </div>
          )}
        </div>

        <div className="ml-auto flex items-center gap-2">
          <button
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors shadow-sm"
            onClick={() => navigate('/explore')}
          >
            <svg className="h-4 w-4 text-gray-500" viewBox="0 0 20 20" fill="currentColor">
              <path
                fillRule="evenodd"
                d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                clipRule="evenodd"
              />
            </svg>
            Explore
          </button>
          <button
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors shadow-sm"
            onClick={() => navigate('/logs')}
            title="Logs"
          >
            <svg className="h-4 w-4 text-gray-500" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M4.5 2A1.5 1.5 0 003 3.5v13A1.5 1.5 0 004.5 18h11a1.5 1.5 0 001.5-1.5V7.621a1.5 1.5 0 00-.44-1.06l-4.12-4.122A1.5 1.5 0 0010.378 2H4.5zm2.25 8.5a.75.75 0 000 1.5h6.5a.75.75 0 000-1.5h-6.5zm0 3a.75.75 0 000 1.5h6.5a.75.75 0 000-1.5h-6.5z" clipRule="evenodd" />
            </svg>
            <span className="hidden sm:inline">Logs</span>
          </button>
          <button
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors shadow-sm"
            onClick={() => navigate('/settings')}
            title="Settings"
          >
            <svg className="h-4 w-4 text-gray-500" viewBox="0 0 20 20" fill="currentColor">
              <path
                fillRule="evenodd"
                d="M7.84 1.804A1 1 0 018.82 1h2.36a1 1 0 01.98.804l.295 1.473c.497.144.971.342 1.416.587l1.25-.834a1 1 0 011.262.125l1.668 1.668a1 1 0 01.125 1.262l-.834 1.25c.245.445.443.919.587 1.416l1.473.294a1 1 0 01.804.98v2.361a1 1 0 01-.804.98l-1.473.295a6.95 6.95 0 01-.587 1.416l.834 1.25a1 1 0 01-.125 1.262l-1.668 1.668a1 1 0 01-1.262.125l-1.25-.834a6.953 6.953 0 01-1.416.587l-.294 1.473a1 1 0 01-.98.804H8.82a1 1 0 01-.98-.804l-.294-1.473a6.957 6.957 0 01-1.416-.587l-1.25.834a1 1 0 01-1.262-.125L1.05 17.02a1 1 0 01-.125-1.262l.834-1.25a6.957 6.957 0 01-.587-1.416l-1.473-.294A1 1 0 01.001 8.82v-2.36a1 1 0 01.804-.98l1.473-.295c.144-.497.342-.971.587-1.416l-.834-1.25a1 1 0 01.125-1.262L4.99 1.05a1 1 0 011.262-.125l1.25.834c.445-.245.919-.443 1.416-.587l.295-1.473zM10 13a3 3 0 100-6 3 3 0 000 6z"
                clipRule="evenodd"
              />
            </svg>
            Settings
          </button>
          <button
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 transition-colors shadow-sm"
            onClick={() => setCreateIssueOpen(true)}
          >
            <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
              <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
            </svg>
            New Issue
          </button>
        </div>
      </header>

      <CreateIssueDialog open={createIssueOpen} onClose={() => setCreateIssueOpen(false)} />
      <CreateProjectDialog open={createProjectOpen} onClose={() => setCreateProjectOpen(false)} />

      <Dialog
        open={deleteConfirmOpen}
        onClose={() => setDeleteConfirmOpen(false)}
        title="Delete Project"
      >
        <div className="space-y-3">
          <p className="text-sm text-gray-600">
            Are you sure you want to delete{' '}
            <span className="font-medium text-gray-900">{currentProject?.name}</span>?
            This will also delete all associated issues.
          </p>

          {deleteProject.isError && (
            <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
              {(deleteProject.error as Error).message}
            </div>
          )}

          <div className="flex justify-end gap-2 pt-1">
            <button
              onClick={() => setDeleteConfirmOpen(false)}
              className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={handleDelete}
              disabled={deleteProject.isPending}
              className="rounded-md bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
            >
              {deleteProject.isPending ? 'Deleting...' : 'Delete'}
            </button>
          </div>
        </div>
      </Dialog>
    </>
  )
}
