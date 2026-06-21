import { useState, type ReactNode } from 'react'
import { useParams, useNavigate, Navigate } from 'react-router-dom'
import {
  BotIcon,
  ClockIcon,
  FileTextIcon,
  FolderTreeIcon,
  GitBranchIcon,
  SettingsIcon,
  SlidersHorizontalIcon,
  TagIcon,
} from 'lucide-react'
import { AiSettingsSection } from './AiSettingsSection'
import { AgentSettingsSection } from './AgentSettingsSection'
import { PreferencesSection } from './PreferencesSection'
import { SystemSettingsSection } from './SystemSettingsSection'
import { WorkflowProfilesSection } from './WorkflowProfilesSection'
import { RepositoriesSection } from './RepositoriesSection'
import { TemplatesSection } from './TemplatesSection'
import { LabelCatalogSection } from './LabelCatalogSection'
import { OnboardingBanner } from './OnboardingBanner'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { useProject, useProjectPath } from '../../../entities/project'
import { SettingsSearch } from '@/features/settings-search'
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from '@/shared/ui/components/tabs'

const VALID_SECTIONS = [
  'ai',
  'agent',
  'repositories',
  'workflows',
  'templates',
  'label-catalog',
  'system',
  'preferences',
] as const
type Section = (typeof VALID_SECTIONS)[number]
const ONBOARDING_DISMISSED_KEY = 'mohist:settings-onboarding-dismissed'

const SECTION_META: { key: Section; label: string; icon: ReactNode }[] = [
  { key: 'ai', label: 'Coder Agent', icon: <BotIcon /> },
  { key: 'agent', label: 'Runtime', icon: <ClockIcon /> },
  { key: 'repositories', label: 'Repositories', icon: <FolderTreeIcon /> },
  { key: 'workflows', label: 'Workflows', icon: <GitBranchIcon /> },
  { key: 'templates', label: 'Templates', icon: <FileTextIcon /> },
  { key: 'label-catalog', label: 'Label catalog', icon: <TagIcon /> },
  { key: 'system', label: 'System', icon: <SettingsIcon /> },
  { key: 'preferences', label: 'Preferences', icon: <SlidersHorizontalIcon /> },
]

function isValidSection(s: string): s is Section {
  return VALID_SECTIONS.includes(s as Section)
}

function SectionContent({ section }: { section: Section }) {
  const { currentProject } = useProject()

  switch (section) {
    case 'ai':
      return <AiSettingsSection />
    case 'agent':
      return <AgentSettingsSection />
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
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const [showOnboarding, setShowOnboarding] = useState(
    () => window.localStorage.getItem(ONBOARDING_DISMISSED_KEY) !== 'true',
  )

  useDocumentTitle('Settings — Mohist')

  function dismissOnboarding() {
    window.localStorage.setItem(ONBOARDING_DISMISSED_KEY, 'true')
    setShowOnboarding(false)
  }

  if (!section || !isValidSection(section)) {
    return <Navigate to={toProjectPath('/settings/ai')} replace />
  }

  return (
    <div className="flex-1 min-h-0 overflow-y-auto">
      <div className="max-w-5xl mx-auto px-4 md:px-6 py-6">
        <h1 className="sr-only">Settings</h1>
        <Tabs
          value={section}
          onValueChange={(value) => navigate(toProjectPath(`/settings/${value}`))}
          orientation="horizontal"
          className="gap-4"
        >
          <TabsList variant="line" className="w-full justify-start overflow-x-auto bg-transparent p-0 border-b rounded-none">
            {SECTION_META.map((s) => (
              <TabsTrigger key={s.key} value={s.key} data-testid={`settings-tab-${s.key}`}>
                {s.icon}
                {s.label}
              </TabsTrigger>
            ))}
          </TabsList>
          {SECTION_META.map((s) => (
            <TabsContent key={s.key} value={s.key} className="mt-2">
              {s.key === 'ai' && showOnboarding ? (
                <OnboardingBanner onDismiss={dismissOnboarding} />
              ) : null}
              <SectionContent section={s.key} />
            </TabsContent>
          ))}
        </Tabs>
      </div>
      <SettingsSearch />
    </div>
  )
}
