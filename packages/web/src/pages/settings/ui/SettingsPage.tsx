import type { ReactNode } from 'react'
import { useParams, useNavigate, Navigate } from 'react-router-dom'
import { BotIcon, ClockIcon, FolderTreeIcon, GitBranchIcon, SettingsIcon } from 'lucide-react'
import { AiSettingsSection } from './AiSettingsSection'
import { AgentSettingsSection } from './AgentSettingsSection'
import { SystemSettingsSection } from './SystemSettingsSection'
import { WorkflowProfilesSection } from './WorkflowProfilesSection'
import { RepositoriesSection } from './RepositoriesSection'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { useProject } from '../../../entities/project'
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from '@/shared/ui/components/tabs'

const VALID_SECTIONS = ['ai', 'agent', 'repositories', 'workflows', 'system'] as const
type Section = (typeof VALID_SECTIONS)[number]

const SECTION_META: { key: Section; label: string; icon: ReactNode }[] = [
  { key: 'ai', label: 'Coder Agent', icon: <BotIcon /> },
  { key: 'agent', label: 'Runtime', icon: <ClockIcon /> },
  { key: 'repositories', label: 'Repositories', icon: <FolderTreeIcon /> },
  { key: 'workflows', label: 'Workflows', icon: <GitBranchIcon /> },
  { key: 'system', label: 'System', icon: <SettingsIcon /> },
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
    case 'system':
      return <SystemSettingsSection />
  }
}

export function SettingsPage() {
  const { section } = useParams<{ section: string }>()
  const navigate = useNavigate()

  useDocumentTitle('Settings — Mohist')

  if (!section || !isValidSection(section)) {
    return <Navigate to="/settings/ai" replace />
  }

  return (
    <div className="flex-1 min-h-0 overflow-y-auto">
      <div className="max-w-5xl mx-auto px-4 md:px-6 py-6">
        <Tabs
          value={section}
          onValueChange={(value) => navigate(`/settings/${value}`)}
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
              <SectionContent section={s.key} />
            </TabsContent>
          ))}
        </Tabs>
      </div>
    </div>
  )
}
