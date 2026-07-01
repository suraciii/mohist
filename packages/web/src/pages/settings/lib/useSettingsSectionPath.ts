import { useCallback } from 'react'
import { projectPath, useProject } from '../../../entities/project'
import { isApplicationSection, type SettingsSectionKey } from './sections'

export function useSettingsSectionPath(): (section: SettingsSectionKey) => string | null {
  const { currentProject } = useProject()
  return useCallback(
    (section: SettingsSectionKey) =>
      isApplicationSection(section)
        ? `/settings/${section}`
        : currentProject
          ? projectPath(currentProject.name, `/settings/${section}`)
          : null,
    [currentProject?.name],
  )
}
