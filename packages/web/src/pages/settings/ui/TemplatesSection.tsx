import { useMemo, useState } from 'react'
import { InfoIcon, PlusIcon, SearchIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import { Input } from '@/shared/ui/components/input'
import { useProject } from '../../../entities/project'
import {
  useDeleteProjectTemplateOverride,
  useProjectTemplates,
  useSystemTemplates,
} from '../../../entities/template'
import type { ProjectTemplate, SystemTemplate } from '../../../entities/template'
import type { SettingsSearchEntry } from '@/features/settings-search'
import { getSectionMeta } from '../lib/sections'
import { SectionState } from './SectionState'
import { SettingsSection } from './SettingsSection'
import { TemplateEditor, type TemplateEditorTarget, type EditorMode } from './TemplateEditor'
import { NewTemplateDialog } from './NewTemplateDialog'

const SOURCE_LABELS: Record<ProjectTemplate['source'], { label: string; tooltip: string; classes: string }> = {
  system: {
    label: 'system',
    tooltip: 'System template (read-only). Use Override to customize it for this project.',
    classes: 'bg-muted text-foreground border border-border',
  },
  'project-override': {
    label: 'projectⓘ',
    tooltip: 'Project override of a system template. The system body is not used.',
    classes: 'bg-amber-50 text-amber-800 border border-amber-200',
  },
  'project-new': {
    label: 'projectⓘ new',
    tooltip: 'Project-unique template. No system template exists for this key.',
    classes: 'bg-sky-50 text-sky-800 border border-sky-200',
  },
}

const templateActionClassName = 'min-h-[44px] px-3 py-2 text-xs'

function SourceLabel({ source }: { source: ProjectTemplate['source'] }) {
  const meta = SOURCE_LABELS[source]
  return (
    <span
      data-testid="template-source-label"
      title={meta.tooltip}
      className={`inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[10px] font-medium ${meta.classes}`}
    >
      {meta.label}
      <InfoIcon className="size-3 opacity-70" />
    </span>
  )
}

function StageBadge({ stage }: { stage: string | null }) {
  if (!stage) return null
  return (
    <span
      data-testid="template-stage-badge"
      title={`Stage: ${stage}`}
      className="inline-flex items-center gap-1 rounded-full border border-border bg-muted/50 px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground"
    >
      <span className="inline-block h-1.5 w-1.5 rounded-full bg-foreground/40" />
      {stage}
    </span>
  )
}

function TagChips({ tags }: { tags: string[] }) {
  if (tags.length === 0) return null
  return (
    <div className="flex flex-wrap items-center gap-1">
      {tags.map((t) => (
        <span
          key={t}
          data-testid="template-tag-chip"
          className="rounded border border-border bg-background px-1.5 py-0.5 text-[10px] text-muted-foreground"
        >
          {t}
        </span>
      ))}
    </div>
  )
}

export const TEMPLATES_DESCRIPTORS: SettingsSearchEntry[] = [
  {
    tab: 'templates',
    label: 'Template search',
    description: 'Filter templates by key, name, tag, or description.',
    placeholder: 'Search by key, name, tag, or description',
    focusTargetId: 'templates-search',
  },
  {
    tab: 'templates',
    label: 'New Template',
    description: 'Create a project-unique prompt template.',
    focusTargetId: 'template-new-button',
  },
]

function matchesSearch(
  template: ProjectTemplate,
  query: string,
): boolean {
  if (!query) return true
  const q = query.toLowerCase()
  if (template.key.toLowerCase().includes(q)) return true
  if (template.displayName.toLowerCase().includes(q)) return true
  if (template.description.toLowerCase().includes(q)) return true
  if (template.tags.some((t) => t.toLowerCase().includes(q))) return true
  return false
}

function TemplateRow({
  template,
  systemByKey,
  onOpenEditor,
  onReset,
  isResetPending,
}: {
  template: ProjectTemplate
  systemByKey: Map<string, SystemTemplate>
  onOpenEditor: (target: TemplateEditorTarget) => void
  onReset: (key: string) => void
  isResetPending: boolean
}) {
  const isSystem = template.source === 'system'
  const isProject = !isSystem
  const isOverridden = template.source === 'project-override'

  function open(mode: EditorMode) {
    if (mode === 'override' && isSystem) {
      const system = systemByKey.get(template.key)
      onOpenEditor({
        mode,
        template,
        initialBody: system?.body ?? template.body,
        initialDisplayName: system?.displayName ?? template.displayName,
        initialDescription: system?.description ?? template.description,
        initialTags: system?.tags ?? template.tags,
        initialStage: system?.stage ?? template.stage,
      })
      return
    }
    onOpenEditor({
      mode,
      template,
      initialBody: template.body,
      initialDisplayName: template.displayName,
      initialDescription: template.description,
      initialTags: template.tags,
      initialStage: template.stage,
    })
  }

  return (
    <CardSection
      data-testid={`template-row-${template.key}`}
      className="space-y-2 p-3"
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0 flex-1 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-mono text-xs text-muted-foreground">{template.key}</span>
            <SourceLabel source={template.source} />
            <StageBadge stage={template.stage} />
          </div>
          <div className="text-sm font-medium text-foreground">{template.displayName}</div>
          {template.description && (
            <p className="text-xs text-muted-foreground line-clamp-2">{template.description}</p>
          )}
          <TagChips tags={template.tags} />
        </div>
        <div className="flex shrink-0 flex-wrap items-center justify-end gap-1">
          {isSystem && (
            <Button
              variant="outline"
              size="sm"
              onClick={() => open('override')}
              className={templateActionClassName}
              data-testid={`template-override-${template.key}`}
            >
              Override
            </Button>
          )}
          {isProject && (
            <Button
              variant="outline"
              size="sm"
              onClick={() => open('edit')}
              className={templateActionClassName}
              data-testid={`template-edit-${template.key}`}
            >
              Edit
            </Button>
          )}
          <Button
            variant="ghost"
            size="sm"
            onClick={() => open('preview')}
            className={templateActionClassName}
            data-testid={`template-preview-${template.key}`}
          >
            Preview
          </Button>
          {isOverridden && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => onReset(template.key)}
              disabled={isResetPending}
              className={templateActionClassName}
              data-testid={`template-reset-${template.key}`}
            >
              Reset
            </Button>
          )}
          {isProject && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => onReset(template.key)}
              disabled={isResetPending}
              data-testid={`template-delete-${template.key}`}
              className={`${templateActionClassName} text-red-700 hover:text-red-800 hover:bg-red-50`}
            >
              Delete
            </Button>
          )}
        </div>
      </div>
    </CardSection>
  )
}

export function TemplatesSection() {
  const { currentProject } = useProject()
  const projectId = currentProject?.id
  const { data: templates, isLoading, isError, refetch } = useProjectTemplates(projectId)
  const { data: systemTemplates } = useSystemTemplates()
  const deleteOverride = useDeleteProjectTemplateOverride(projectId)
  const [search, setSearch] = useState('')
  const [editorTarget, setEditorTarget] = useState<TemplateEditorTarget | null>(null)
  const [newDialogOpen, setNewDialogOpen] = useState(false)
  const { label: sectionLabel, description: sectionDescription } = getSectionMeta('templates')

  const systemByKey = useMemo(() => {
    const map = new Map<string, SystemTemplate>()
    for (const t of systemTemplates ?? []) map.set(t.key, t)
    return map
  }, [systemTemplates])

  const filteredTemplates = useMemo(() => {
    if (!templates) return []
    return templates.filter((t) => matchesSearch(t, search))
  }, [templates, search])

  if (!currentProject) {
    return (
      <div
        data-testid="templates-no-project"
        className="rounded-md border border-dashed bg-muted/30 p-6 text-center text-sm text-muted-foreground"
      >
        No project selected
      </div>
    )
  }

  return (
    <SettingsSection
      title={sectionLabel}
      description={sectionDescription}
    >
      <div className="flex justify-end">
        <Button
          id="template-new-button"
          size="sm"
          onClick={() => setNewDialogOpen(true)}
          data-testid="template-new-button"
          className={templateActionClassName}
        >
          <PlusIcon />
          New Template
        </Button>
      </div>

      <div className="relative">
        <SearchIcon className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted-foreground" />
        <Input
          id="templates-search"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search by key, name, tag, or description"
          data-testid="template-search"
          className="min-h-11 pl-7 text-sm"
        />
      </div>

      {isLoading ? (
        <SectionState variant="loading" title="Templates" skeletonRows={3} />
      ) : isError ? (
        <SectionState
          variant="error"
          title="Templates"
          message="Failed to load templates."
          onRetry={() => refetch()}
        />
      ) : filteredTemplates.length === 0 ? (
        <SectionState
          variant="empty"
          title="Templates"
          description={
            templates && templates.length > 0
              ? 'No templates match the current search.'
              : 'No templates available for this project.'
          }
        />
      ) : (
        <div className="space-y-2">
          {filteredTemplates.map((t) => (
            <TemplateRow
              key={t.key}
              template={t}
              systemByKey={systemByKey}
              onOpenEditor={setEditorTarget}
              onReset={(key) => deleteOverride.mutate({ key })}
              isResetPending={deleteOverride.isPending}
            />
          ))}
        </div>
      )}

      {editorTarget && (
        <TemplateEditor
          key={`${editorTarget.template.key}:${editorTarget.mode}`}
          projectId={projectId!}
          target={editorTarget}
          onClose={() => setEditorTarget(null)}
        />
      )}

      <NewTemplateDialog
        open={newDialogOpen}
        projectId={projectId!}
        onClose={() => setNewDialogOpen(false)}
      />
    </SettingsSection>
  )
}
