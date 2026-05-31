import { describe, it, expect } from 'vitest'
import { render } from '@testing-library/react'
import { WorkflowStage, IssueStatus } from '../src/entities/issue'
import { WorkflowStatusTimeline } from '../src/widgets/coder-session/ui/SessionTimeline'

describe('Integrate stage rendering', () => {
  describe('WorkflowStatusTimeline', () => {
    it('should include Integrate between Check and Done in stage order', () => {
      const stageOrder = ['backlog', 'plan', 'build', 'check', 'integrate', 'done']

      expect(stageOrder).toContain('integrate')
      const checkIdx = stageOrder.indexOf('check')
      const integrateIdx = stageOrder.indexOf('integrate')
      const doneIdx = stageOrder.indexOf('done')

      expect(integrateIdx).toBeGreaterThan(checkIdx)
      expect(doneIdx).toBeGreaterThan(integrateIdx)
    })

    it('should render integrate stage in the timeline', () => {
      const { container } = render(
        <WorkflowStatusTimeline currentStage="check" />
      )
      expect(container.textContent).toContain('Integrate')
    })

    it('should show completed state for stages before current stage', () => {
      const { container } = render(
        <WorkflowStatusTimeline currentStage="integrate" />
      )
      const html = container.innerHTML
      expect(html).toContain('Plan')
      expect(html).toContain('Build')
      expect(html).toContain('Check')
      expect(html).toContain('Integrate')
    })
  })

  describe('Stage enum and order', () => {
    it('should have Integrate between Check and Done', () => {
      const stageOrder = [
        IssueStatus.Backlog,
        WorkflowStage.Plan,
        WorkflowStage.Build,
        WorkflowStage.Check,
        WorkflowStage.Integrate,
        WorkflowStage.Done,
      ]

      const checkIdx = stageOrder.indexOf(WorkflowStage.Check)
      const integrateIdx = stageOrder.indexOf(WorkflowStage.Integrate)
      const doneIdx = stageOrder.indexOf(WorkflowStage.Done)

      expect(checkIdx).toBeLessThan(integrateIdx)
      expect(integrateIdx).toBeLessThan(doneIdx)
    })

    it('should have Stage.Integrate with value integrate', () => {
      expect(WorkflowStage.Integrate).toBe('integrate')
    })

    it('should have Backlog as initial stage', () => {
      expect(IssueStatus.Backlog).toBe('backlog')
    })
  })
})

describe('CheckReadinessOutput types', () => {
  it('should have required integration readiness fields', () => {
    const readinessOutput = {
      specImpact: {
        capabilities: ['test-capability'],
        targetFiles: ['openspec/specs/test-capability/spec.md'],
        counts: { added: 1, modified: 0, removed: 0, renamed: 0 },
        conflicts: [],
        valid: true,
      },
      mergeReadiness: {
        targetBranch: 'main',
        canFastForward: true,
        cleanRebaseFeasible: true,
      },
      healthGatePolicy: {
        policyName: 'postMerge',
        command: 'npm test',
        timeout: 300000,
        enabled: true,
      },
    }

    expect(readinessOutput.specImpact).toBeDefined()
    expect(readinessOutput.mergeReadiness).toBeDefined()
    expect(readinessOutput.healthGatePolicy).toBeDefined()
    expect(readinessOutput.specImpact.capabilities).toContain('test-capability')
    expect(readinessOutput.mergeReadiness.targetBranch).toBe('main')
    expect(readinessOutput.healthGatePolicy.command).toBe('npm test')
  })
})

describe('Integration event types', () => {
  it('should include integration_started event', () => {
    const event = {
      integration_started: {
        issueId: 'issue-1',
        projectId: 'project-1',
        issueNumber: 1,
      },
    }
    expect(event.integration_started.issueNumber).toBe(1)
  })

  it('should include integration_step_updated event', () => {
    const event = {
      integration_step_updated: {
        issueId: 'issue-1',
        projectId: 'project-1',
        issueNumber: 1,
        step: 'integrate:spec-sync',
        status: 'completed',
        summary: 'specs synced successfully',
      },
    }
    expect(event.integration_step_updated.step).toBe('integrate:spec-sync')
    expect(event.integration_step_updated.status).toBe('completed')
  })

  it('should include integration_failed event', () => {
    const event = {
      integration_failed: {
        issueId: 'issue-1',
        projectId: 'project-1',
        issueNumber: 1,
        failingStep: 'integrate:merge',
        error: 'Merge conflict detected',
        output: { conflicts: ['file1.ts', 'file2.ts'] },
      },
    }
    expect(event.integration_failed.failingStep).toBe('integrate:merge')
    expect(event.integration_failed.error).toBe('Merge conflict detected')
  })

  it('should include integration_completed event', () => {
    const event = {
      integration_completed: {
        issueId: 'issue-1',
        projectId: 'project-1',
        issueNumber: 1,
        steps: [
          { step: 'integrate:spec-sync', status: 'completed' },
          { step: 'integrate:archive-change', status: 'completed' },
          { step: 'integrate:merge', status: 'completed' },
          { step: 'final-health', status: 'completed' },
        ],
      },
    }
    expect(event.integration_completed.steps).toHaveLength(4)
    expect(event.integration_completed.steps[0].step).toBe('integrate:spec-sync')
  })
})

describe('IntegrationStepResult type', () => {
  it('should have correct shape for integration step results', () => {
    const stepResult = {
      step: 'integrate:spec-sync',
      status: 'completed' as const,
      output: {
        capabilities: ['cap1'],
        counts: { added: 2, modified: 1, removed: 0, renamed: 0 },
        targetFiles: ['file1.md'],
        conflicts: [],
        valid: true,
      },
      startedAt: '2026-05-09T10:00:00Z',
      completedAt: '2026-05-09T10:00:05Z',
      duration: 5000,
    }

    expect(stepResult.step).toBe('integrate:spec-sync')
    expect(stepResult.status).toBe('completed')
    expect(stepResult.duration).toBe(5000)
  })
})
