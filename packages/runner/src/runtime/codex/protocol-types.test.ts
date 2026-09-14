import { describe, expect, it } from 'vitest'
import {
  CODEX_LOCKED_METHODS,
  isCodexInitializeRequest,
  isCodexInitializeResult,
  isCodexItemEvent,
  isCodexLockedMethod,
  isCodexModelListResult,
  isCodexServerRequest,
  isCodexThreadCompactStartRequest,
  isCodexThreadResumeRequest,
  isCodexThreadStartRequest,
  isCodexThreadStartResult,
  isCodexThreadStatusEvent,
  isCodexTurnCompletedEvent,
  isCodexTurnInputItem,
  isCodexTurnInterruptRequest,
  isCodexTurnInterruptResult,
  isCodexTurnStartRequest,
  isCodexTurnSteerRequest,
} from './protocol-types.js'

describe('Codex locked v2 protocol subset', () => {
  it('declares the exact locked method set', () => {
    expect([...CODEX_LOCKED_METHODS]).toEqual([
      'initialize',
      'initialized',
      'thread/start',
      'thread/resume',
      'turn/start',
      'turn/steer',
      'turn/interrupt',
      'thread/compact/start',
      'model/list',
    ])
  })

  it('rejects methods outside the locked subset', () => {
    expect(isCodexLockedMethod('turn/foo')).toBe(false)
    expect(isCodexLockedMethod('apps/list')).toBe(false)
    expect(isCodexLockedMethod('connectors/list')).toBe(false)
    expect(isCodexLockedMethod('review/start')).toBe(false)
  })

  it('accepts methods inside the locked subset', () => {
    for (const method of CODEX_LOCKED_METHODS) {
      expect(isCodexLockedMethod(method)).toBe(true)
    }
  })

  it('matches initialize requests only when the envelope and params are valid', () => {
    expect(
      isCodexInitializeRequest({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: 'v2' } }),
    ).toBe(true)
    expect(
      isCodexInitializeRequest({
        jsonrpc: '2.0',
        id: 1,
        method: 'initialize',
        params: { clientInfo: { name: 'mohist', version: '0.1.0' } },
      }),
    ).toBe(true)
    expect(isCodexInitializeRequest({ jsonrpc: '2.0', id: 1, method: 'initialize' })).toBe(true)
    expect(isCodexInitializeRequest({ jsonrpc: '2.0', id: 1, method: 'turn/start' })).toBe(false)
    expect(
      isCodexInitializeRequest({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: 42 } }),
    ).toBe(false)
    expect(
      isCodexInitializeRequest({
        jsonrpc: '2.0',
        id: 1,
        method: 'initialize',
        params: { clientInfo: { name: 7, version: 'x' } },
      }),
    ).toBe(false)
    expect(isCodexInitializeRequest({ jsonrpc: '2.0', id: 1, method: 'initialize', params: 'broken' })).toBe(false)
  })

  it('matches initialize results only when codexHome is a string', () => {
    expect(
      isCodexInitializeResult({
        jsonrpc: '2.0',
        id: 1,
        result: { protocolVersion: 'v2', codexHome: '/runner/.mohist/codex', userAgent: 'codex/0.153.0' },
      }),
    ).toBe(true)
    expect(
      isCodexInitializeResult({
        jsonrpc: '2.0',
        id: 1,
        result: { protocolVersion: 'v2', codexHome: '/runner/.mohist/codex' },
      }),
    ).toBe(true)
    expect(isCodexInitializeResult({ jsonrpc: '2.0', id: 1, result: { protocolVersion: 'v2' } })).toBe(false)
    expect(isCodexInitializeResult({ jsonrpc: '2.0', id: 1, result: { protocolVersion: 'v2', codexHome: 42 } })).toBe(
      false,
    )
  })

  it('matches thread/start requests only with a string cwd', () => {
    expect(
      isCodexThreadStartRequest({
        jsonrpc: '2.0',
        id: 1,
        method: 'thread/start',
        params: { cwd: '/work', model: 'gpt-5', reasoningEffort: 'medium' },
      }),
    ).toBe(true)
    expect(isCodexThreadStartRequest({ jsonrpc: '2.0', id: 1, method: 'thread/start', params: { cwd: 7 } })).toBe(false)
    expect(
      isCodexThreadStartRequest({ jsonrpc: '2.0', id: 1, method: 'thread/resume', params: { cwd: '/work' } }),
    ).toBe(false)
  })

  it('matches thread/start results only with a string threadId', () => {
    expect(
      isCodexThreadStartResult({
        jsonrpc: '2.0',
        id: 1,
        result: { threadId: 'thr_1', cwd: '/work', model: 'gpt-5', reasoningEffort: 'medium' },
      }),
    ).toBe(true)
    expect(isCodexThreadStartResult({ jsonrpc: '2.0', id: 1, result: { threadId: 42, cwd: '/work' } })).toBe(false)
  })

  it('matches thread/resume requests only with threadId + cwd + optional excludeTurns', () => {
    expect(
      isCodexThreadResumeRequest({
        jsonrpc: '2.0',
        id: 2,
        method: 'thread/resume',
        params: { threadId: 'thr_1', cwd: '/work', excludeTurns: true },
      }),
    ).toBe(true)
    expect(
      isCodexThreadResumeRequest({
        jsonrpc: '2.0',
        id: 2,
        method: 'thread/resume',
        params: { threadId: 'thr_1', cwd: '/work' },
      }),
    ).toBe(true)
    expect(
      isCodexThreadResumeRequest({
        jsonrpc: '2.0',
        id: 2,
        method: 'thread/resume',
        params: { threadId: 'thr_1', cwd: '/work', excludeTurns: 'yes' },
      }),
    ).toBe(false)
  })

  it('matches thread/compact/start requests only with threadId + cwd', () => {
    expect(
      isCodexThreadCompactStartRequest({
        jsonrpc: '2.0',
        id: 3,
        method: 'thread/compact/start',
        params: { threadId: 'thr_1', cwd: '/work' },
      }),
    ).toBe(true)
    expect(
      isCodexThreadCompactStartRequest({
        jsonrpc: '2.0',
        id: 3,
        method: 'thread/compact/start',
        params: { threadId: 'thr_1', cwd: 7 },
      }),
    ).toBe(false)
  })

  it('matches turn/start requests only with threadId + valid input items', () => {
    expect(
      isCodexTurnStartRequest({
        jsonrpc: '2.0',
        id: 4,
        method: 'turn/start',
        params: {
          threadId: 'thr_1',
          input: [{ type: 'text', text: 'hello' }],
          model: 'gpt-5',
          reasoningEffort: 'low',
          clientUserMessageId: 'sess_input_42',
        },
      }),
    ).toBe(true)
    expect(
      isCodexTurnStartRequest({
        jsonrpc: '2.0',
        id: 4,
        method: 'turn/start',
        params: { threadId: 'thr_1', input: [{ type: 'text' }] },
      }),
    ).toBe(false)
    expect(
      isCodexTurnStartRequest({
        jsonrpc: '2.0',
        id: 4,
        method: 'turn/start',
        params: { threadId: 'thr_1', input: [{ type: 'image', url: 'data:image/png;base64,...', mime: 'image/png' }] },
      }),
    ).toBe(true)
    expect(
      isCodexTurnStartRequest({
        jsonrpc: '2.0',
        id: 4,
        method: 'turn/start',
        params: { threadId: 'thr_1', input: [{ type: 'image', url: 'data:image/png;base64,...', mime: 7 }] },
      }),
    ).toBe(false)
    expect(
      isCodexTurnStartRequest({
        jsonrpc: '2.0',
        id: 4,
        method: 'turn/start',
        params: { threadId: 'thr_1', input: 'broken' },
      }),
    ).toBe(false)
    expect(
      isCodexTurnStartRequest({
        jsonrpc: '2.0',
        id: 4,
        method: 'turn/start',
        params: { threadId: 42, input: [{ type: 'text', text: 'hello' }] },
      }),
    ).toBe(false)
  })

  it('discriminates every Turn input item variant by exact type', () => {
    expect(isCodexTurnInputItem({ type: 'text', text: 'hi' })).toBe(true)
    expect(isCodexTurnInputItem({ type: 'text' })).toBe(false)
    expect(isCodexTurnInputItem({ type: 'image', url: 'data:url', mime: 'image/png' })).toBe(true)
    expect(isCodexTurnInputItem({ type: 'image', url: 7, mime: 'image/png' })).toBe(false)
    expect(isCodexTurnInputItem({ type: 'localImage', path: '/work/img.png' })).toBe(true)
    expect(isCodexTurnInputItem({ type: 'video', url: 'data:url', mime: 'video/mp4' })).toBe(false)
    expect(isCodexTurnInputItem(null)).toBe(false)
  })

  it('matches turn/steer requests with frozen threadId + turnId + input', () => {
    expect(
      isCodexTurnSteerRequest({
        jsonrpc: '2.0',
        id: 5,
        method: 'turn/steer',
        params: { threadId: 'thr_1', turnId: 'turn_1', input: [{ type: 'text', text: 'continue' }] },
      }),
    ).toBe(true)
    expect(
      isCodexTurnSteerRequest({
        jsonrpc: '2.0',
        id: 5,
        method: 'turn/steer',
        params: { threadId: 'thr_1', turnId: 'turn_1', input: 'broken' },
      }),
    ).toBe(false)
    expect(
      isCodexTurnSteerRequest({
        jsonrpc: '2.0',
        id: 5,
        method: 'turn/steer',
        params: { threadId: 'thr_1', turnId: 7, input: [] },
      }),
    ).toBe(false)
  })

  it('matches turn/interrupt requests and results', () => {
    expect(
      isCodexTurnInterruptRequest({
        jsonrpc: '2.0',
        id: 6,
        method: 'turn/interrupt',
        params: { threadId: 'thr_1', turnId: 'turn_1' },
      }),
    ).toBe(true)
    expect(
      isCodexTurnInterruptRequest({
        jsonrpc: '2.0',
        id: 6,
        method: 'turn/interrupt',
        params: { threadId: 'thr_1' },
      }),
    ).toBe(false)
    expect(
      isCodexTurnInterruptResult({
        jsonrpc: '2.0',
        id: 6,
        result: { threadId: 'thr_1', turnId: 'turn_1', accepted: true },
      }),
    ).toBe(true)
    expect(
      isCodexTurnInterruptResult({
        jsonrpc: '2.0',
        id: 6,
        result: { threadId: 'thr_1', turnId: 'turn_1', accepted: 'yes' },
      }),
    ).toBe(false)
  })

  it('matches model/list results only with a string-array models and boolean complete', () => {
    expect(
      isCodexModelListResult({
        jsonrpc: '2.0',
        id: 7,
        result: { models: [{ id: 'gpt-5' }], complete: true, nextCursor: null },
      }),
    ).toBe(true)
    expect(
      isCodexModelListResult({
        jsonrpc: '2.0',
        id: 7,
        result: { models: [{ id: 'gpt-5' }], complete: true },
      }),
    ).toBe(true)
    expect(
      isCodexModelListResult({
        jsonrpc: '2.0',
        id: 7,
        result: { models: 'not-an-array', complete: true },
      }),
    ).toBe(false)
    expect(
      isCodexModelListResult({
        jsonrpc: '2.0',
        id: 7,
        result: { models: [{ id: 'gpt-5' }], complete: 'yes' },
      }),
    ).toBe(false)
  })

  it('recognizes server-initiated requests only when the envelope is well-formed', () => {
    expect(
      isCodexServerRequest({
        jsonrpc: '2.0',
        id: 99,
        method: 'item/tool/requestApproval',
        params: { threadId: 'thr_1', turnId: 'turn_1' },
      }),
    ).toBe(true)
    expect(
      isCodexServerRequest({
        jsonrpc: '2.0',
        id: 99,
        method: 'item/tool/requestApproval',
      }),
    ).toBe(true)
    expect(isCodexServerRequest({ jsonrpc: '2.0', id: 99, method: 7 })).toBe(false)
    expect(isCodexServerRequest({ jsonrpc: '2.0', method: 'x' })).toBe(false)
    expect(isCodexServerRequest({ id: 99, method: 'x' })).toBe(false)
  })

  it('accepts any item-event discriminant but rejects non-objects', () => {
    expect(isCodexItemEvent({ type: 'agentMessage', text: 'hello' })).toBe(true)
    expect(isCodexItemEvent({ type: 'reasoning', summary: 'think' })).toBe(true)
    expect(isCodexItemEvent({ type: 'fileChange', path: '/work/a.txt', kind: 'modify' })).toBe(true)
    expect(isCodexItemEvent({ type: 'usage', inputTokens: 1, outputTokens: 2 })).toBe(true)
    expect(isCodexItemEvent({ type: 'unknown-but-diagnostic' })).toBe(true)
    expect(isCodexItemEvent(null)).toBe(false)
    expect(isCodexItemEvent({})).toBe(false)
  })

  it('only matches turn/completed events with the locked terminal statuses', () => {
    expect(
      isCodexTurnCompletedEvent({ type: 'turn/completed', threadId: 'thr_1', turnId: 'turn_1', status: 'completed' }),
    ).toBe(true)
    expect(
      isCodexTurnCompletedEvent({ type: 'turn/completed', threadId: 'thr_1', turnId: 'turn_1', status: 'failed' }),
    ).toBe(true)
    expect(
      isCodexTurnCompletedEvent({ type: 'turn/completed', threadId: 'thr_1', turnId: 'turn_1', status: 'interrupted' }),
    ).toBe(true)
    expect(
      isCodexTurnCompletedEvent({ type: 'turn/completed', threadId: 'thr_1', turnId: 'turn_1', status: 'paused' }),
    ).toBe(false)
    expect(
      isCodexTurnCompletedEvent({ type: 'turn/aborted', threadId: 'thr_1', turnId: 'turn_1', status: 'completed' }),
    ).toBe(false)
    expect(
      isCodexTurnCompletedEvent({ type: 'turn/completed', threadId: 'thr_1', turnId: 7, status: 'completed' }),
    ).toBe(false)
  })

  it('matches thread/status events only with a string type + threadId + status', () => {
    expect(isCodexThreadStatusEvent({ type: 'thread/status', threadId: 'thr_1', status: 'idle' })).toBe(true)
    expect(
      isCodexThreadStatusEvent({ type: 'thread/status', threadId: 'thr_1', turnId: 'turn_1', status: 'busy' }),
    ).toBe(true)
    expect(isCodexThreadStatusEvent({ type: 'thread/status', threadId: 'thr_1', status: 7 })).toBe(false)
    expect(isCodexThreadStatusEvent({ type: 'thread/status', threadId: 7, status: 'idle' })).toBe(false)
  })
})
