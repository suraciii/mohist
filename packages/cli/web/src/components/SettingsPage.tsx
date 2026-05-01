import type { ReactNode } from 'react'
import { useNavigate, useParams, Navigate } from 'react-router-dom'
import { AiSettingsSection } from './AiSettingsSection'
import { AgentSettingsSection } from './AgentSettingsSection'
import { SystemSettingsSection } from './SystemSettingsSection'
import { useDocumentTitle } from '../hooks/useDocumentTitle'

const VALID_SECTIONS = ['ai', 'agent', 'system'] as const
type Section = (typeof VALID_SECTIONS)[number]

const SECTION_META: { key: Section; label: string; icon: ReactNode }[] = [
  {
    key: 'ai',
    label: 'AI',
    icon: (
      <svg className="w-4 h-4" viewBox="0 0 20 20" fill="currentColor">
        <path d="M10 2a.75.75 0 01.75.75v1.5a.75.75 0 01-1.5 0v-1.5A.75.75 0 0110 2zM10 15a.75.75 0 01.75.75v1.5a.75.75 0 01-1.5 0v-1.5A.75.75 0 0110 15zM10 7a3 3 0 100 6 3 3 0 000-6zM15.657 5.404a.75.75 0 10-1.06-1.06l-1.061 1.06a.75.75 0 001.06 1.06l1.06-1.06zM6.464 14.596a.75.75 0 10-1.06-1.06l-1.06 1.06a.75.75 0 001.06 1.06l1.06-1.06zM18 10a.75.75 0 01-.75.75h-1.5a.75.75 0 010-1.5h1.5A.75.75 0 0118 10zM5 10a.75.75 0 01-.75.75h-1.5a.75.75 0 010-1.5h1.5A.75.75 0 015 10zM14.596 15.657a.75.75 0 001.06-1.06l-1.06-1.061a.75.75 0 10-1.06 1.06l1.06 1.06zM5.404 6.464a.75.75 0 001.06-1.06l-1.06-1.06a.75.75 0 10-1.061 1.06l1.06 1.06z" />
      </svg>
    ),
  },
  {
    key: 'agent',
    label: 'Agent',
    icon: (
      <svg className="w-4 h-4" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm.75-13a.75.75 0 00-1.5 0v5c0 .414.336.75.75.75h4a.75.75 0 000-1.5h-3.25V5z" clipRule="evenodd" />
      </svg>
    ),
  },
  {
    key: 'system',
    label: 'System',
    icon: (
      <svg className="w-4 h-4" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M7.84 1.804A1 1 0 018.82 1h2.36a1 1 0 01.98.804l.331 1.652a6.993 6.993 0 011.929 1.115l1.598-.54a1 1 0 011.186.447l1.18 2.044a1 1 0 01-.205 1.251l-1.267 1.113a7.047 7.047 0 010 2.228l1.267 1.113a1 1 0 01.206 1.25l-1.18 2.045a1 1 0 01-1.187.447l-1.598-.54a6.993 6.993 0 01-1.929 1.115l-.33 1.652a1 1 0 01-.98.804H8.82a1 1 0 01-.98-.804l-.331-1.652a6.993 6.993 0 01-1.929-1.115l-1.598.54a1 1 0 01-1.186-.447l-1.18-2.044a1 1 0 01.205-1.251l1.267-1.114a7.05 7.05 0 010-2.227L1.821 7.773a1 1 0 01-.206-1.25l1.18-2.045a1 1 0 011.187-.447l1.598.54A6.993 6.993 0 017.51 3.456l.33-1.652zM10 13a3 3 0 100-6 3 3 0 000 6z" clipRule="evenodd" />
      </svg>
    ),
  },
]

function isValidSection(s: string): s is Section {
  return VALID_SECTIONS.includes(s as Section)
}

function SectionContent({ section }: { section: Section }) {
  switch (section) {
    case 'ai':
      return <AiSettingsSection />
    case 'agent':
      return <AgentSettingsSection />
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
    <div className="flex-1 bg-gray-50">
      <div className="max-w-5xl mx-auto px-4 md:px-6 py-6">
        <div className="mb-6">
          <h1 className="text-xl font-semibold text-gray-900">Settings</h1>
        </div>

        <div className="md:hidden mb-4">
          <select
            value={section}
            onChange={(e) => navigate(`/settings/${e.target.value}`)}
            className="w-full px-4 py-3 text-sm font-medium text-gray-700 bg-white border border-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 min-h-[44px]"
          >
            {SECTION_META.map((s) => (
              <option key={s.key} value={s.key}>
                {s.label}
              </option>
            ))}
          </select>
        </div>

        <div className="hidden md:flex gap-6">
          <nav className="w-48 shrink-0">
            <div className="sticky top-6 space-y-1">
              {SECTION_META.map((s) => (
                <button
                  key={s.key}
                  onClick={() => navigate(`/settings/${s.key}`)}
                  className={`flex items-center gap-2 w-full px-3 py-2 text-sm font-medium rounded-md transition-colors ${
                    section === s.key
                      ? 'bg-blue-50 text-blue-700'
                      : 'text-gray-600 hover:text-gray-900 hover:bg-gray-100'
                  }`}
                >
                  <span className={section === s.key ? 'text-blue-500' : 'text-gray-400'}>
                    {s.icon}
                  </span>
                  {s.label}
                </button>
              ))}
            </div>
          </nav>

          <div className="flex-1 min-w-0">
            <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-6">
              <SectionContent section={section} />
            </div>
          </div>
        </div>

        <div className="md:hidden">
          <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-6">
            <SectionContent section={section} />
          </div>
        </div>
      </div>
    </div>
  )
}
