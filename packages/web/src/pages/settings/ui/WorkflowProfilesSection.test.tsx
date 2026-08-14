import { beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '../../../../tests/test-utils'
import userEvent from '@testing-library/user-event'
import { setScopedProperty } from '../../../../tests/support/scoped-property'
import {
  WorkflowProfilesSection as DefaultWorkflowProfilesSection,
  WORKFLOW_DESCRIPTORS,
  type WorkflowActionCatalogHook,
  type WorkflowProfileHook,
  type WorkflowProfileAgentActionMutationHook,
  type WorkflowProfilesSectionComponents,
  type WorkflowProfilesSectionDataHook,
} from './WorkflowProfilesSection'

const SYSTEM_TEMPLATES = [
  {
    id: 'mohist/local',
    name: 'Mohist Local',
    description:
      'Full Mohist pipeline for shipping user-visible changes end-to-end.\nStages: plan (proposal, specs, design, tasks, self-review) → build → check (AI review, merge readiness) → integrate (spec sync, archive, merge).\nRequires human approval at the plan and check stages, with the workflow merging the working branch into the project base branch on completion.\nTypical duration: 20-60 minutes for a focused change.\nBest suited for: new features, user-visible behavior changes, changes that need a design document or spec delta.\nNot suited for: simple bug fixes (use quick-fix), exploration or throwaway prototypes (use experiment), or pure refactors with no behavior change.',
    isDefault: true,
  },
  {
    id: 'mohist/quick-fix',
    name: 'Mohist Quick Fix',
    description:
      'Lightweight workflow for small, low-risk, fast-turnaround changes.\nSuited for: simple bug fixes, single-file or few-line corrections, trivial test updates, and obvious defects with a known fix.\nGoal is a fast, low-friction path: minimal planning artifacts, no design document, no spec delta, and lighter review.\nTypical duration: 5-15 minutes for a focused fix.\nNot suited for: new user-visible features (use mohist/local), exploration or throwaway prototypes (use experiment), or changes that need a design/spec delta (use mohist/local).',
    isDefault: false,
  },
  {
    id: 'mohist/experiment',
    name: 'Mohist Experiment',
    description: 'Short single-line description for test.',
    isDefault: false,
  },
]

const CUSTOM_PROFILE = {
  id: 'team/custom',
  displayName: 'Team Custom',
  description: 'Project-specific workflow.',
  isDefault: false,
  isBuiltIn: false,
}

const DEFAULT_DETAIL = {
  id: 'mohist/local',
  name: 'Mohist Local',
  description: SYSTEM_TEMPLATES[0].description,
  isDefault: true,
  agentAction: 'mohist/opencode',
  agentRuntime: 'opencode' as const,
  yaml: 'description: |\n  Full Mohist pipeline for shipping user-visible changes end-to-end.\nstages:\n  - stage: plan\n    tasks: []\n    checks: []\n',
  stages: [
    { stage: 'plan', requiresApproval: true, tasks: ['proposal'], checks: [] },
    { stage: 'build', requiresApproval: false, tasks: ['implement'], checks: [] },
  ],
}

const overflowByTestId = new Map<string, boolean>()

const DETAILS = {
  'mohist/local': { ...DEFAULT_DETAIL, displayName: DEFAULT_DETAIL.name },
  'mohist/quick-fix': {
    id: 'mohist/quick-fix',
    displayName: 'Mohist Quick Fix',
    description: SYSTEM_TEMPLATES[1].description,
    isDefault: false,
    yaml: 'description: quick-fix\nstages:\n  - stage: check\n    checks:\n      - merge-ready\n  - stage: integrate\n    requiresApproval: true\n',
    stages: [
      { stage: 'check', requiresApproval: false, tasks: [], checks: ['merge-ready'] },
      { stage: 'integrate', requiresApproval: true, tasks: [], checks: [] },
    ],
  },
  'mohist/experiment': {
    id: 'mohist/experiment',
    displayName: 'Mohist Experiment',
    description: SYSTEM_TEMPLATES[2].description,
    isDefault: false,
    yaml: 'description: experiment\nstages: []\n',
    stages: [],
  },
}

const disableRequests: string[] = []
const agentActionRequests: Array<{ profileId: string; agentAction: string | null }> = []

const dataHook: WorkflowProfilesSectionDataHook = () => ({
  allProfiles: [
    ...SYSTEM_TEMPLATES.map((profile) => ({
      id: profile.id,
      displayName: profile.name,
      description: profile.description,
      isDefault: profile.isDefault,
      isBuiltIn: true,
    })),
    CUSTOM_PROFILE,
  ],
  profilesLoading: false,
  profilesError: false,
  projectProfile: {
    projectId: 'test-project',
    defaultTemplateId: null,
    disabledWorkflowProfileIds: [],
  },
  projectProfileLoading: false,
  projectProfileError: false,
  disableMutation: { mutate: (profileId) => disableRequests.push(profileId), isPending: false },
  enableMutation: { mutate: () => undefined, isPending: false },
})

const profileHook: WorkflowProfileHook = (profileId) => ({
  data: profileId ? DETAILS[profileId as keyof typeof DETAILS] : undefined,
  isLoading: false,
  isError: false,
})

const actionCatalogHook: WorkflowActionCatalogHook = () => ({
  data: {
    actions: [
      { name: 'mohist/opencode', capabilities: ['agent-turn'] },
      { name: 'mohist/pi', capabilities: ['agent-turn'] },
      { name: 'mohist/git-push', capabilities: [] },
    ],
  },
  isLoading: false,
  isError: false,
})

const agentActionMutationHook: WorkflowProfileAgentActionMutationHook = () => ({
  mutate: (variables) => agentActionRequests.push(variables),
  isPending: false,
  error: null,
})

const components: WorkflowProfilesSectionComponents = {
  ProjectDefaultWorkflowControl: () => <div data-testid="project-default-workflow-control" />,
}

function WorkflowProfilesSection() {
  return (
    <DefaultWorkflowProfilesSection
      dataHook={dataHook}
      profileHook={profileHook}
      actionCatalogHook={actionCatalogHook}
      agentActionMutationHook={agentActionMutationHook}
      components={components}
    />
  )
}

beforeEach(() => {
  overflowByTestId.clear()
  disableRequests.length = 0
  agentActionRequests.length = 0
  setScopedProperty(HTMLElement.prototype, 'clientHeight', {
    configurable: true,
    get() {
      return 32
    },
  })
  setScopedProperty(HTMLElement.prototype, 'scrollHeight', {
    configurable: true,
    get() {
      if (!(this instanceof HTMLElement)) return 32
      return overflowByTestId.get(this.dataset.testid ?? '') ? 64 : 32
    },
  })
})

describe('WorkflowProfilesSection', () => {
  describe('Profile list (cards)', () => {
    it('disables an enabled non-last profile through the switch', async () => {
      render(<WorkflowProfilesSection />)

      const quickFixCard = await waitFor(() => screen.getByTestId('workflow-profile-mohist/quick-fix'))
      fireEvent.click(within(quickFixCard).getByRole('switch', { name: 'Disable workflow profile Mohist Quick Fix' }))

      await waitFor(() => {
        expect(disableRequests).toEqual(['mohist/quick-fix'])
      })
      expect(screen.queryByText('At least one workflow profile must remain enabled.')).not.toBeInTheDocument()
    })

    it('does not render the built-in enable/disable switch for a custom profile', async () => {
      render(<WorkflowProfilesSection />)

      const customCard = await waitFor(() => screen.getByTestId('workflow-profile-team/custom'))

      expect(within(customCard).queryByRole('switch')).not.toBeInTheDocument()
      expect(within(customCard).getByRole('button', { name: 'View details' })).toBeInTheDocument()
    })

    it('renders one card per profile with display name, id, and multi-line description', async () => {
      render(<WorkflowProfilesSection />)

      for (const profile of SYSTEM_TEMPLATES) {
        await waitFor(() => expect(screen.getByTestId(`workflow-profile-${profile.id}`)).toBeInTheDocument())
      }

      const defaultCard = screen.getByTestId('workflow-profile-mohist/local')
      const defaultDescription = within(defaultCard).getByTestId('workflow-profile-mohist/local-description')
      expect(defaultDescription.className).toContain('whitespace-pre-line')
      expect(defaultDescription.textContent).toContain(
        'Full Mohist pipeline for shipping user-visible changes end-to-end.',
      )
      expect(defaultDescription.textContent).toContain('Best suited for: new features')

      const quickFixCard = screen.getByTestId('workflow-profile-mohist/quick-fix')
      const quickFixDescription = within(quickFixCard).getByTestId('workflow-profile-mohist/quick-fix-description')
      expect(quickFixDescription.className).toContain('whitespace-pre-line')
      expect(quickFixDescription.textContent).toContain(
        'Lightweight workflow for small, low-risk, fast-turnaround changes.',
      )

      expect(within(defaultCard).getByText('System default')).toBeInTheDocument()
      expect(within(quickFixCard).queryByText('System default')).not.toBeInTheDocument()

      expect(within(defaultCard).getByText('mohist/local')).toBeInTheDocument()
      expect(within(quickFixCard).getByText('mohist/quick-fix')).toBeInTheDocument()

      await waitFor(() => {
        expect(within(defaultCard).getByText('plan')).toBeInTheDocument()
        expect(within(defaultCard).getByText('build')).toBeInTheDocument()
      })

      await waitFor(() => {
        expect(within(quickFixCard).getByText('check')).toBeInTheDocument()
        expect(within(quickFixCard).getByText('integrate')).toBeInTheDocument()
      })

      const experimentCard = screen.getByTestId('workflow-profile-mohist/experiment')
      await waitFor(() => {
        expect(within(experimentCard).getByText('No stages')).toBeInTheDocument()
      })
    })

    it('renders single-line descriptions without truncation or layout issues', async () => {
      render(<WorkflowProfilesSection />)

      const experimentCard = await waitFor(() => screen.getByTestId('workflow-profile-mohist/experiment'))
      const description = within(experimentCard).getByTestId('workflow-profile-mohist/experiment-description')
      expect(description.textContent).toBe('Short single-line description for test.')
      expect(description.className).toContain('whitespace-pre-line')
      expect(screen.queryByText('Read more')).not.toBeInTheDocument()
    })

    it('keeps Read more as an independent keyboard action without opening detail', async () => {
      overflowByTestId.set('workflow-profile-mohist/local-description', true)

      render(<WorkflowProfilesSection />)

      const defaultDescription = await waitFor(() => screen.getByTestId('workflow-profile-mohist/local-description'))
      const readMore = screen.getByRole('button', { name: 'Read more' })

      fireEvent.keyDown(readMore, { key: 'Enter' })
      fireEvent.keyUp(readMore, { key: 'Enter' })
      fireEvent.click(readMore)

      expect(defaultDescription.className).not.toContain('line-clamp-2')
      expect(screen.queryByTestId('workflow-profile-description')).not.toBeInTheDocument()
      expect(screen.getByTestId('workflow-profile-mohist/local')).toBeInTheDocument()
    })

    it('shows Read more only for overflowing descriptions and expands the text', async () => {
      overflowByTestId.set('workflow-profile-mohist/local-description', true)
      overflowByTestId.set('workflow-profile-mohist/quick-fix-description', false)
      overflowByTestId.set('workflow-profile-mohist/experiment-description', false)

      render(<WorkflowProfilesSection />)

      const defaultDescription = await waitFor(() => screen.getByTestId('workflow-profile-mohist/local-description'))

      expect(screen.getByText('Read more')).toBeInTheDocument()
      expect(defaultDescription.className).toContain('line-clamp-2')
      expect(screen.queryAllByText('Read more')).toHaveLength(1)

      fireEvent.click(screen.getByText('Read more'))

      expect(defaultDescription.className).not.toContain('line-clamp-2')
      expect(screen.queryByText('Read more')).not.toBeInTheDocument()
    })
  })

  describe('Profile detail', () => {
    it('selects Agent Actions exclusively from agent-turn catalog capabilities', async () => {
      const user = userEvent.setup()
      render(<WorkflowProfilesSection />)

      await waitFor(() => expect(screen.getByTestId('workflow-profile-mohist/local')).toBeInTheDocument())
      await user.click(
        within(screen.getByTestId('workflow-profile-mohist/local')).getByRole('button', { name: 'View details' }),
      )
      await user.click(await screen.findByTestId('workflow-profile-agent-action-selector'))

      expect(await screen.findByRole('option', { name: 'mohist/opencode' })).toBeInTheDocument()
      expect(screen.getByRole('option', { name: 'mohist/pi' })).toBeInTheDocument()
      expect(screen.queryByRole('option', { name: 'mohist/git-push' })).not.toBeInTheDocument()

      await user.click(screen.getByRole('option', { name: 'mohist/pi' }))
      expect(agentActionRequests).toEqual([{ profileId: 'mohist/local', agentAction: 'mohist/pi' }])
    })

    it('does not show an Agent Action selector when the Profile has no binding', async () => {
      const user = userEvent.setup()
      render(<WorkflowProfilesSection />)

      await waitFor(() => expect(screen.getByTestId('workflow-profile-mohist/quick-fix')).toBeInTheDocument())
      await user.click(
        within(screen.getByTestId('workflow-profile-mohist/quick-fix')).getByRole('button', { name: 'View details' }),
      )

      expect(screen.queryByTestId('workflow-profile-agent-action-selector')).not.toBeInTheDocument()
    })

    it('shows the full multi-line description at the top with readable formatting and whitespace-pre-line', async () => {
      render(<WorkflowProfilesSection />)

      await waitFor(() => expect(screen.getByTestId('workflow-profile-mohist/local')).toBeInTheDocument())

      fireEvent.click(
        within(screen.getByTestId('workflow-profile-mohist/local')).getByRole('button', {
          name: 'View details',
        }),
      )

      const description = await waitFor(() => screen.getByTestId('workflow-profile-description'))
      expect(description.className).toContain('whitespace-pre-line')
      expect(description.className).not.toContain('font-mono')
      expect(description.className).not.toContain('line-clamp')
      expect(description.textContent).toContain('Full Mohist pipeline for shipping user-visible changes end-to-end.')
      expect(description.textContent).toContain('Best suited for: new features')

      expect(screen.getByText('Stages')).toBeInTheDocument()
      expect(screen.getByText('Shared Stage Definition (YAML)')).toBeInTheDocument()
    })

    it('orders description above stages and YAML in the DOM', async () => {
      const container = document.createElement('div')
      document.body.appendChild(container)
      render(<WorkflowProfilesSection />, { container })

      await waitFor(() => expect(screen.getByTestId('workflow-profile-mohist/local')).toBeInTheDocument())
      fireEvent.click(
        within(screen.getByTestId('workflow-profile-mohist/local')).getByRole('button', {
          name: 'View details',
        }),
      )

      await waitFor(() => expect(screen.getByTestId('workflow-profile-description')).toBeInTheDocument())

      const description = screen.getByTestId('workflow-profile-description')
      const stagesHeading = screen.getByText('Stages')
      const yamlHeading = screen.getByText('Shared Stage Definition (YAML)')

      const DOC_ORDER = Node.DOCUMENT_POSITION_FOLLOWING
      expect(description.compareDocumentPosition(stagesHeading) & DOC_ORDER).toBeTruthy()
      expect(stagesHeading.compareDocumentPosition(yamlHeading) & DOC_ORDER).toBeTruthy()
    })

    it('falls back to the All profiles view when the back button is clicked', async () => {
      render(<WorkflowProfilesSection />)

      await waitFor(() => expect(screen.getByTestId('workflow-profile-mohist/local')).toBeInTheDocument())
      fireEvent.click(
        within(screen.getByTestId('workflow-profile-mohist/local')).getByRole('button', {
          name: 'View details',
        }),
      )

      const backButton = await waitFor(() => screen.getByTestId('workflow-profile-back'))
      fireEvent.click(backButton)

      await waitFor(() => expect(screen.getByTestId('workflow-profile-mohist/local')).toBeInTheDocument())
    })
  })
})

describe('WORKFLOW_DESCRIPTORS', () => {
  it('contains at least one workflow-related entry', () => {
    expect(WORKFLOW_DESCRIPTORS.length).toBeGreaterThan(0)
    for (const entry of WORKFLOW_DESCRIPTORS) {
      expect(entry.tab).toBe('workflows')
      expect(entry.label).toBeTruthy()
      expect(entry.description).toBeTruthy()
      expect(entry.focusTargetId).toBeTruthy()
    }
  })

  it('includes a Workflow Profiles entry that navigates to the section', () => {
    const profileEntry = WORKFLOW_DESCRIPTORS.find((e) => e.focusTargetId === 'workflow-profiles-section')
    expect(profileEntry).toBeDefined()
    expect(profileEntry!.label).toBe('Workflow Profiles')
  })

  it('includes a Project Default Workflow entry', () => {
    const defaultEntry = WORKFLOW_DESCRIPTORS.find((e) => e.focusTargetId === 'project-default-workflow')
    expect(defaultEntry).toBeDefined()
    expect(defaultEntry!.label).toBe('Project Default Workflow')
  })
})
