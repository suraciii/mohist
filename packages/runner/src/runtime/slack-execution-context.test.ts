import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import {
  NON_SLACK_EXECUTION_SOURCE,
  PUBLISHED_SLACK_SKILL_HASH,
  PUBLISHED_SLACK_SKILL_NAME,
  PUBLISHED_SLACK_SKILL_VERSION,
  SLACK_EXECUTION_SOURCE,
  readExecutionSourceContext,
  type SlackExecutionContext,
} from './slack-execution-context.js'

const instructions = readFileSync(
  new URL('../../../server/src/Mohist.Server/Agent/Services/Assets/mohist-slack-collaboration.skill.md', import.meta.url),
  'utf8',
)
const preChangeInstructions = readFileSync(
  new URL('./fixtures/pre-change-mohist-slack-collaboration.skill.md', import.meta.url),
  'utf8',
)

type ContextOverrides = {
  readonly version?: number
  readonly replyAnchor?: Partial<SlackExecutionContext['replyAnchor']>
  readonly collaborationSkill?: Partial<SlackExecutionContext['collaborationSkill']>
}

function validContext(overrides: ContextOverrides = {}): SlackExecutionContext {
  const replyAnchor = {
    workspaceId: 'T1',
    conversationId: 'C1',
    threadRootMessageId: '100.0',
    triggeringMessageId: '101.0',
    initiatingMemberId: 'U1',
    connectionId: 'connection_1',
    sessionId: 'session_1',
    dispatchRef: 'dispatch_1',
    ...(overrides.replyAnchor ?? {}),
  } as SlackExecutionContext['replyAnchor']
  const collaborationSkill = {
    name: PUBLISHED_SLACK_SKILL_NAME,
    version: PUBLISHED_SLACK_SKILL_VERSION,
    instructions,
    contentHash: createHash('sha256').update(instructions, 'utf8').digest('hex'),
    ...(overrides.collaborationSkill ?? {}),
  } as SlackExecutionContext['collaborationSkill']
  return {
    version: overrides.version ?? 1,
    replyAnchor,
    collaborationSkill,
  }
}

describe('Slack execution source/context validation', () => {
  it('accepts the published Slack context and exposes no mutable destination choice', () => {
    const result = readExecutionSourceContext({
      executionSource: SLACK_EXECUTION_SOURCE,
      slackExecutionContext: validContext(),
    }, { strict: true })

    expect(result).toMatchObject({ kind: 'resolved', source: SLACK_EXECUTION_SOURCE })
    if (result.kind !== 'resolved') throw new Error('expected a valid Slack context')
    expect(result.slackExecutionContext?.replyAnchor.threadRootMessageId).toBe('100.0')
    expect(result.slackExecutionContext?.collaborationSkill.contentHash).toBe(PUBLISHED_SLACK_SKILL_HASH)
  })

  it.each([
    ['unknown source', { executionSource: 'web', slackExecutionContext: null }],
    ['Slack source without context', { executionSource: SLACK_EXECUTION_SOURCE }],
    ['Slack source with null context', { executionSource: SLACK_EXECUTION_SOURCE, slackExecutionContext: null }],
    ['non-Slack source with context', { executionSource: NON_SLACK_EXECUTION_SOURCE, slackExecutionContext: validContext() }],
    ['non-object context', { executionSource: SLACK_EXECUTION_SOURCE, slackExecutionContext: [] }],
    ['unsupported version', { executionSource: SLACK_EXECUTION_SOURCE, slackExecutionContext: validContext({ version: 2 }) }],
    ['empty anchor', { executionSource: SLACK_EXECUTION_SOURCE, slackExecutionContext: validContext({ replyAnchor: { conversationId: '' } }) }],
    ['wrong Skill identity', { executionSource: SLACK_EXECUTION_SOURCE, slackExecutionContext: validContext({ collaborationSkill: { name: 'other-skill' } }) }],
    ['uppercase digest', { executionSource: SLACK_EXECUTION_SOURCE, slackExecutionContext: validContext({ collaborationSkill: { contentHash: PUBLISHED_SLACK_SKILL_HASH.toUpperCase() } }) }],
    ['modified Skill body', { executionSource: SLACK_EXECUTION_SOURCE, slackExecutionContext: validContext({ collaborationSkill: { instructions: `${instructions}changed` } }) }],
  ])('rejects %s', (_name, payload) => {
    expect(readExecutionSourceContext(payload, { strict: true }).kind).toBe('invalid')
  })

  it('rejects an explicit null source even while compatibility mode is enabled', () => {
    expect(readExecutionSourceContext({ executionSource: null, slackExecutionContext: validContext() }).kind).toBe('invalid')
  })

  it('accepts the actual pre-change source-less Slack snapshot through compatibility mode', () => {
    const contentHash = createHash('sha256').update(preChangeInstructions, 'utf8').digest('hex')
    expect(contentHash).toBe('de3272639a1d390f3dcf915e65b6c057bf0b9eb91c51545572eb1e484c8c1a22')

    const legacy = readExecutionSourceContext({
      slackExecutionContext: validContext({
        collaborationSkill: {
          instructions: preChangeInstructions,
          contentHash,
        },
      }),
    })

    expect(legacy).toMatchObject({
      kind: 'legacy',
      slackExecutionContext: { collaborationSkill: { instructions: preChangeInstructions, contentHash } },
    })
    expect(readExecutionSourceContext({
      executionSource: SLACK_EXECUTION_SOURCE,
      slackExecutionContext: validContext({
        collaborationSkill: { instructions: preChangeInstructions, contentHash },
      }),
    }).kind).toBe('invalid')
  })

  it('accepts omitted source only through the bounded compatibility path without relabeling it', () => {
    const legacy = readExecutionSourceContext({ slackExecutionContext: validContext() })
    expect(legacy).toMatchObject({ kind: 'legacy', slackExecutionContext: validContext() })
    expect(readExecutionSourceContext({}, { strict: true }).kind).toBe('invalid')
  })
})
