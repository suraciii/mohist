import { describe, expect, it } from 'vitest'
import type { AgentDetailEventMap } from './types'

type ModelResolvedPayload = AgentDetailEventMap['model.resolved']

function modelResolvedPayloadKeys(payload: ModelResolvedPayload): Array<keyof ModelResolvedPayload> {
  return Object.keys(payload) as Array<keyof ModelResolvedPayload>
}

describe('AgentDetailEventMap model.resolved', () => {
  it('uses resolvedModel as the field name for the resolved model name', () => {
    const payload: ModelResolvedPayload = { resolvedModel: 'anthropic/claude-sonnet-4-20250514' }

    expect(payload.resolvedModel).toBe('anthropic/claude-sonnet-4-20250514')
  })

  it('exposes only the resolvedModel field on a baseline payload', () => {
    const payload: ModelResolvedPayload = { resolvedModel: 'openai/gpt-5.6' }

    expect(modelResolvedPayloadKeys(payload)).toEqual(['resolvedModel'])
  })
})
