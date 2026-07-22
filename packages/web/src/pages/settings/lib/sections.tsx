import type { ReactNode } from 'react'
import {
  SETTINGS_SECTION_KEYS,
  isApplicationSection,
  isProjectSection,
  isSettingsSectionKey,
  sectionScope,
  type SettingsSectionKey,
  type SettingsSectionScope,
} from '../../../shared/config/settings-sections'
import {
  BotIcon,
  ClockIcon,
  FileTextIcon,
  FolderTreeIcon,
  GitBranchIcon,
  InboxIcon,
  SettingsIcon,
  SlidersHorizontalIcon,
  TagIcon,
} from 'lucide-react'

export interface SettingsSectionMeta {
  key: SettingsSectionKey
  label: string
  icon: ReactNode
  scope: SettingsSectionScope
  description?: string
}

export const SETTINGS_SECTIONS: readonly SettingsSectionMeta[] = [
  { key: 'ai', label: 'Coder Agent', icon: <BotIcon />, scope: 'application' },
  {
    key: 'agent',
    label: 'Runtime',
    icon: <ClockIcon />,
    scope: 'application',
    description: 'Configure how Mohist schedules external coder agent sessions.',
  },
  { key: 'repositories', label: 'Repositories', icon: <FolderTreeIcon />, scope: 'project' },
  {
    key: 'workflows',
    label: 'Workflows',
    icon: <GitBranchIcon />,
    scope: 'project',
    description: 'Choose the workflow new issues inherit for this project, then browse the read-only system catalog below.',
  },
  {
    key: 'templates',
    label: 'Templates',
    icon: <FileTextIcon />,
    scope: 'project',
    description: 'Manage prompt templates for this project. Override system templates or add project-unique keys.',
  },
  {
    key: 'label-catalog',
    label: 'Label catalog',
    icon: <TagIcon />,
    scope: 'project',
    description: 'Define the labels your project suggests for issues. This catalog is advisory — issues can still carry any free-form label, and edits here do not change existing issue labels.',
  },
  { key: 'inbox', label: 'Inbox', icon: <InboxIcon />, scope: 'project' },
  {
    key: 'system',
    label: 'System',
    icon: <SettingsIcon />,
    scope: 'application',
    description: 'Logging, runtime identity, and local-source update status.',
  },
  {
    key: 'preferences',
    label: 'Preferences',
    icon: <SlidersHorizontalIcon />,
    scope: 'application',
    description: 'Real user preferences and read-only reference information. System facts live on the System tab.',
  },
]

export function getSectionMeta(key: SettingsSectionKey): SettingsSectionMeta {
  const meta = SETTINGS_SECTIONS.find((entry) => entry.key === key)
  if (!meta) {
    throw new Error(`Unknown settings section: ${key}`)
  }
  return meta
}

export {
  SETTINGS_SECTION_KEYS,
  isApplicationSection,
  isProjectSection,
  isSettingsSectionKey,
  sectionScope,
}
export type { SettingsSectionKey, SettingsSectionScope }
