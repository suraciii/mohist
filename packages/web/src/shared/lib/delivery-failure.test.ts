import { describe, expect, it } from 'vitest'
import {
  DELIVERY_FAILURE_KINDS,
  getDeliveryFailureGuidance,
  isDeliveryFailureKind,
  resolveDeliveryFailureFromMessage,
  resolveDeliveryFailureFromOutput,
} from './delivery-failure'

describe('delivery-failure kinds', () => {
  it('exposes the three PR-specific failure kinds', () => {
    expect(isDeliveryFailureKind('config-error')).toBe(true)
    expect(isDeliveryFailureKind('protection-conflict')).toBe(true)
    expect(isDeliveryFailureKind('pr-state-conflict')).toBe(true)
  })

  it('lists the PR-specific kinds in DELIVERY_FAILURE_KINDS', () => {
    expect(DELIVERY_FAILURE_KINDS).toContain('config-error')
    expect(DELIVERY_FAILURE_KINDS).toContain('protection-conflict')
    expect(DELIVERY_FAILURE_KINDS).toContain('pr-state-conflict')
  })
})

describe('PR-specific delivery-failure guidance', () => {
  it('config-error maps to environment-config guidance and is non-retryable', () => {
    const guidance = getDeliveryFailureGuidance('config-error')
    expect(guidance.failureKind).toBe('config-error')
    expect(guidance.retryable).toBe(false)
    expect(guidance.label).toMatch(/environment|misconfigured|gh|cli/i)
    expect(guidance.nextAction).toMatch(/gh/i)
    expect(guidance.nextAction).toMatch(/auth login/i)
    expect(guidance.nextAction).toMatch(/install/i)
    expect(guidance.nextAction).toMatch(/not.*auto.*retry|not retry|environment fix|will not auto-retry/i)
  })

  it('protection-conflict maps to branch-protection guidance and is non-retryable', () => {
    const guidance = getDeliveryFailureGuidance('protection-conflict')
    expect(guidance.failureKind).toBe('protection-conflict')
    expect(guidance.retryable).toBe(false)
    expect(guidance.label).toMatch(/branch protection|configuration|protection/i)
    expect(guidance.nextAction).toMatch(/protection|status check|review|branch.?protection/i)
    expect(guidance.nextAction).toMatch(/not.*auto.*retry|will not auto-retry/i)
  })

  it('pr-state-conflict maps to external-state-change guidance and is non-retryable', () => {
    const guidance = getDeliveryFailureGuidance('pr-state-conflict')
    expect(guidance.failureKind).toBe('pr-state-conflict')
    expect(guidance.retryable).toBe(false)
    expect(guidance.label).toMatch(/external|state|pull request|pr/i)
    expect(guidance.nextAction).toMatch(/external|state|closed|re-open|abandon/i)
    expect(guidance.nextAction).toMatch(/not.*auto.*retry|will not auto-retry/i)
  })
})

describe('PR-specific delivery-failure resolution from output', () => {
  it('resolves config-error from a failureKind field on the output JSON', () => {
    const resolution = resolveDeliveryFailureFromOutput({
      kind: 'publish-via-pr',
      failureKind: 'config-error',
      message: 'gh not installed',
    })
    expect(resolution.failureKind).toBe('config-error')
    expect(resolution.guidance?.nextAction).toMatch(/gh auth login/)
  })

  it('resolves base-moved from an errorCode field on split PR action output', () => {
    const resolution = resolveDeliveryFailureFromOutput({
      kind: 'merge-pull-request',
      errorCode: 'base-moved',
      message: 'GitHub reports this pull request is not mergeable',
    })
    expect(resolution.failureKind).toBe('base-moved')
    expect(resolution.guidance?.retryable).toBe(true)
  })

  it('resolves protection-conflict from a failureKind field on the output JSON', () => {
    const resolution = resolveDeliveryFailureFromOutput({
      kind: 'publish-via-pr',
      failureKind: 'protection-conflict',
      message: 'required status checks are not satisfied',
    })
    expect(resolution.failureKind).toBe('protection-conflict')
    expect(resolution.guidance?.nextAction).toMatch(/protection/)
  })

  it('resolves pr-state-conflict from a failureKind field on the output JSON', () => {
    const resolution = resolveDeliveryFailureFromOutput({
      kind: 'publish-via-pr',
      failureKind: 'pr-state-conflict',
      message: 'PR was closed externally',
    })
    expect(resolution.failureKind).toBe('pr-state-conflict')
    expect(resolution.guidance?.nextAction).toMatch(/external|state|closed/)
  })

  it('resolves the new kinds from a JSON-encoded output string', () => {
    const output = JSON.stringify({
      kind: 'publish-via-pr',
      failureKind: 'config-error',
      message: 'gh missing',
    })
    const resolution = resolveDeliveryFailureFromOutput(output)
    expect(resolution.failureKind).toBe('config-error')
  })

  it('resolves from a parenthesized message fallback', () => {
    const resolution = resolveDeliveryFailureFromMessage('publish failed (pr-state-conflict): closed')
    expect(resolution.failureKind).toBe('pr-state-conflict')
  })
})

describe('existing failure kinds retain their guidance', () => {
  it('conflict guidance is unchanged in shape', () => {
    const guidance = getDeliveryFailureGuidance('conflict')
    expect(guidance.failureKind).toBe('conflict')
    expect(guidance.label).toBe('Conflict needs attention')
    expect(guidance.nextAction).toMatch(/resolve them on the issue branch/)
    expect(guidance.retryable).toBe(false)
  })

  it('base-moved guidance is unchanged in shape', () => {
    const guidance = getDeliveryFailureGuidance('base-moved')
    expect(guidance.failureKind).toBe('base-moved')
    expect(guidance.label).toBe('Base branch moved')
    expect(guidance.nextAction).toMatch(/Prepare the branch again/)
    expect(guidance.retryable).toBe(true)
  })

  it('retry-safe guidance is unchanged in shape', () => {
    const guidance = getDeliveryFailureGuidance('retry-safe')
    expect(guidance.failureKind).toBe('retry-safe')
    expect(guidance.label).toBe('Transient failure')
    expect(guidance.retryable).toBe(true)
  })

  it('branch-invariant-violation guidance is unchanged in shape', () => {
    const guidance = getDeliveryFailureGuidance('branch-invariant-violation')
    expect(guidance.failureKind).toBe('branch-invariant-violation')
    expect(guidance.retryable).toBe(true)
  })

  it('workspace-setup kind guidance is present', () => {
    const guidance = getDeliveryFailureGuidance('workspace-setup')
    expect(guidance.failureKind).toBe('workspace-setup')
    expect(guidance.label).toBe('Workflow workspace setup failure')
    expect(guidance.retryable).toBe(false)
  })
})
