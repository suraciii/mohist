// @vitest-environment jsdom
import {
  afterAll,
  afterEach,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
} from 'vitest'
import { fireEvent, render, screen, waitFor, within } from './test-utils'
import { WorkflowProfilesSection } from '../src/pages/settings/ui/WorkflowProfilesSection'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'

const SYSTEM_TEMPLATES = [
  {
    id: 'mohist/default',
    name: 'Mohist Default',
    description:
      'Full Mohist pipeline for shipping user-visible changes end-to-end.\nStages: plan (proposal, specs, design, tasks, self-review) → build → check (AI review, merge readiness) → integrate (spec sync, archive, merge).\nRequires human approval at the plan and check stages, with the workflow merging the working branch into the project base branch on completion.\nTypical duration: 20-60 minutes for a focused change.\nBest suited for: new features, user-visible behavior changes, changes that need a design document or spec delta.\nNot suited for: simple bug fixes (use quick-fix), exploration or throwaway prototypes (use experiment), or pure refactors with no behavior change.',
    isDefault: true,
  },
  {
    id: 'mohist/quick-fix',
    name: 'Mohist Quick Fix',
    description:
      'Lightweight workflow for small, low-risk, fast-turnaround changes.\nSuited for: simple bug fixes, single-file or few-line corrections, trivial test updates, and obvious defects with a known fix.\nGoal is a fast, low-friction path: minimal planning artifacts, no design document, no spec delta, and lighter review.\nTypical duration: 5-15 minutes for a focused fix.\nNot suited for: new user-visible features (use mohist/default), exploration or throwaway prototypes (use experiment), or changes that need a design/spec delta (use mohist/default).',
    isDefault: false,
  },
  {
    id: 'mohist/experiment',
    name: 'Mohist Experiment',
    description: 'Short single-line description for test.',
    isDefault: false,
  },
]

const DEFAULT_DETAIL = {
  id: 'mohist/default',
  name: 'Mohist Default',
  description: SYSTEM_TEMPLATES[0].description,
  isDefault: true,
  yaml: 'description: |\n  Full Mohist pipeline for shipping user-visible changes end-to-end.\nstages:\n  - stage: plan\n    tasks: []\n    checks: []\n',
  stages: [
    { stage: 'plan', requiresApproval: true, tasks: ['proposal'], checks: [] },
    { stage: 'build', requiresApproval: false, tasks: ['implement'], checks: [] },
  ],
}

const handlers = [
  http.get('/api/workflow-templates/system', () =>
    HttpResponse.json({ success: true, data: SYSTEM_TEMPLATES }),
  ),
  http.get('/api/workflow-templates/system/mohist%2Fdefault', () =>
    HttpResponse.json({ success: true, data: DEFAULT_DETAIL }),
  ),
  http.get('/api/workflow-templates/system/mohist%2Fquick-fix', () =>
    HttpResponse.json({
      success: true,
      data: {
        id: 'mohist/quick-fix',
        name: 'Mohist Quick Fix',
        description: SYSTEM_TEMPLATES[1].description,
        isDefault: false,
        yaml: 'description: quick-fix\nstages: []\n',
        stages: [],
      },
    }),
  ),
  http.get('/api/workflow-templates/system/mohist%2Fexperiment', () =>
    HttpResponse.json({
      success: true,
      data: {
        id: 'mohist/experiment',
        name: 'Mohist Experiment',
        description: SYSTEM_TEMPLATES[2].description,
        isDefault: false,
        yaml: 'description: experiment\nstages: []\n',
        stages: [],
      },
    }),
  ),
]

const server = setupServer(...handlers)

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
})

afterAll(() => {
  server.close()
})

beforeEach(() => {
  server.resetHandlers(...handlers)
})

afterEach(() => {
  server.resetHandlers(...handlers)
})

describe('WorkflowProfilesSection', () => {
  describe('Profile list (cards)', () => {
    it('renders one card per profile with display name, id, and multi-line description', async () => {
      render(<WorkflowProfilesSection />)

      for (const profile of SYSTEM_TEMPLATES) {
        await waitFor(() =>
          expect(screen.getByTestId(`workflow-profile-${profile.id}`)).toBeInTheDocument(),
        )
      }

      const defaultCard = screen.getByTestId('workflow-profile-mohist/default')
      const defaultDescription = within(defaultCard).getByTestId(
        'workflow-profile-mohist/default-description',
      )
      expect(defaultDescription.className).toContain('whitespace-pre-line')
      expect(defaultDescription.textContent).toContain('Full Mohist pipeline for shipping user-visible changes end-to-end.')
      expect(defaultDescription.textContent).toContain('Best suited for: new features')

      const quickFixCard = screen.getByTestId('workflow-profile-mohist/quick-fix')
      const quickFixDescription = within(quickFixCard).getByTestId(
        'workflow-profile-mohist/quick-fix-description',
      )
      expect(quickFixDescription.className).toContain('whitespace-pre-line')
      expect(quickFixDescription.textContent).toContain('Lightweight workflow for small, low-risk, fast-turnaround changes.')

      expect(within(defaultCard).getByText('Default')).toBeInTheDocument()
      expect(within(quickFixCard).queryByText('Default')).not.toBeInTheDocument()

      expect(within(defaultCard).getByText('mohist/default')).toBeInTheDocument()
      expect(within(quickFixCard).getByText('mohist/quick-fix')).toBeInTheDocument()
    })

    it('renders single-line descriptions without truncation or layout issues', async () => {
      render(<WorkflowProfilesSection />)

      const experimentCard = await waitFor(() =>
        screen.getByTestId('workflow-profile-mohist/experiment'),
      )
      const description = within(experimentCard).getByTestId(
        'workflow-profile-mohist/experiment-description',
      )
      expect(description.textContent).toBe('Short single-line description for test.')
      expect(description.className).toContain('whitespace-pre-line')
    })
  })

  describe('Profile detail', () => {
    it('shows the full multi-line description at the top with readable formatting and whitespace-pre-line', async () => {
      render(<WorkflowProfilesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('workflow-profile-mohist/default')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('workflow-profile-mohist/default'))

      const description = await waitFor(() =>
        screen.getByTestId('workflow-profile-description'),
      )
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

      await waitFor(() =>
        expect(screen.getByTestId('workflow-profile-mohist/default')).toBeInTheDocument(),
      )
      fireEvent.click(screen.getByTestId('workflow-profile-mohist/default'))

      await waitFor(() =>
        expect(screen.getByTestId('workflow-profile-description')).toBeInTheDocument(),
      )

      const description = screen.getByTestId('workflow-profile-description')
      const stagesHeading = screen.getByText('Stages')
      const yamlHeading = screen.getByText('Shared Stage Definition (YAML)')

      const DOC_ORDER = Node.DOCUMENT_POSITION_FOLLOWING
      expect(description.compareDocumentPosition(stagesHeading) & DOC_ORDER).toBeTruthy()
      expect(stagesHeading.compareDocumentPosition(yamlHeading) & DOC_ORDER).toBeTruthy()
    })

    it('falls back to the All profiles view when the back button is clicked', async () => {
      render(<WorkflowProfilesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('workflow-profile-mohist/default')).toBeInTheDocument(),
      )
      fireEvent.click(screen.getByTestId('workflow-profile-mohist/default'))

      const backButton = await waitFor(() =>
        screen.getByTestId('workflow-profile-back'),
      )
      fireEvent.click(backButton)

      await waitFor(() =>
        expect(screen.getByTestId('workflow-profile-mohist/default')).toBeInTheDocument(),
      )
    })
  })
})
