import { useParams, Navigate } from 'react-router-dom'
import { AiSettingsSection } from './AiSettingsSection'
import { AgentSettingsSection } from './AgentSettingsSection'
import { InboxSubscriptionSection } from './InboxSubscriptionSection'
import { PreferencesSection } from './PreferencesSection'
import { SystemSettingsSection } from './SystemSettingsSection'
import { WorkflowProfilesSection } from './WorkflowProfilesSection'
import { RepositoriesSection } from './RepositoriesSection'
import { TemplatesSection } from './TemplatesSection'
import { LabelCatalogSection } from './LabelCatalogSection'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { useProject } from '../../../entities/project'
import { SettingsSearch } from '@/features/settings-search'
import {
  isSettingsSectionKey,
  type SettingsSectionKey,
} from '../lib/sections'
import { SettingsSubNav } from './SettingsSubNav'

function SectionContent({ section }: { section: SettingsSectionKey }) {
  const { currentProject } = useProject()

  switch (section) {
    case 'ai':
      return <AiSettingsSection />
    case 'agent':
      return <AgentSettingsSection />
    case 'inbox':
      return currentProject ? (
        <InboxSubscriptionSection />
      ) : (
        <div className="text-sm text-muted-foreground">No project selected</div>
      )
    case 'repositories':
      return currentProject ? (
        <RepositoriesSection projectId={currentProject.id} />
      ) : (
        <div className="text-sm text-muted-foreground">No project selected</div>
      )
    case 'workflows':
      return <WorkflowProfilesSection />
    case 'templates':
      return <TemplatesSection />
    case 'label-catalog':
      return currentProject ? (
        <LabelCatalogSection />
      ) : (
        <div className="text-sm text-muted-foreground">No project selected</div>
      )
    case 'system':
      return <SystemSettingsSection />
    case 'preferences':
      return <PreferencesSection />
  }
}

export function SettingsPage() {
  const { section } = useParams<{ section: string }>()

  useDocumentTitle('Settings — Mohist')

  if (!section || !isSettingsSectionKey(section)) {
    return <Navigate to="/settings/ai" replace />
  }

  return (
    <div className="flex-1 min-h-0 overflow-y-auto">
      <div className="max-w-5xl mx-auto px-4 md:px-6 py-6">
        <h1 className="sr-only">Settings</h1>
        <div className="flex flex-col gap-6 md:flex-row">
          <SettingsSubNav activeSection={section} />
          <div className="flex-1 min-w-0" data-testid="settings-section">
            <SectionContent section={section} />
          </div>
        </div>
      </div>
      <SettingsSearch />
    </div>
  )
}
