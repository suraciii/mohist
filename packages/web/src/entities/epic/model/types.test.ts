import { describe, expect, it } from 'vitest'
import { EpicStatus, parseEpicStatus } from './types'

describe('EpicStatus', () => {
  it('exposes the lifecycle enum members in the expected order', () => {
    expect(Object.values(EpicStatus)).toEqual([
      EpicStatus.Idle,
      EpicStatus.Running,
      EpicStatus.Paused,
      EpicStatus.Done,
      EpicStatus.Closed,
    ])
  })

  it('does not include the legacy Active value', () => {
    expect((EpicStatus as Record<string, string>).Active).toBeUndefined()
  })
})

describe('parseEpicStatus', () => {
  it('parses "idle" to EpicStatus.Idle', () => {
    expect(parseEpicStatus('idle')).toBe(EpicStatus.Idle)
  })

  it('parses "running" to EpicStatus.Running', () => {
    expect(parseEpicStatus('running')).toBe(EpicStatus.Running)
  })

  it('parses "paused" to EpicStatus.Paused', () => {
    expect(parseEpicStatus('paused')).toBe(EpicStatus.Paused)
  })

  it('parses "done" to EpicStatus.Done', () => {
    expect(parseEpicStatus('done')).toBe(EpicStatus.Done)
  })

  it('parses "closed" to EpicStatus.Closed', () => {
    expect(parseEpicStatus('closed')).toBe(EpicStatus.Closed)
  })

  it('treats legacy "active" as EpicStatus.Idle', () => {
    expect(parseEpicStatus('active')).toBe(EpicStatus.Idle)
  })

  it('handles case-insensitive inputs', () => {
    expect(parseEpicStatus('IDLE')).toBe(EpicStatus.Idle)
    expect(parseEpicStatus('Running')).toBe(EpicStatus.Running)
    expect(parseEpicStatus('PAUSED')).toBe(EpicStatus.Paused)
  })

  it('falls back to Idle for null/undefined', () => {
    expect(parseEpicStatus(null)).toBe(EpicStatus.Idle)
    expect(parseEpicStatus(undefined)).toBe(EpicStatus.Idle)
  })

  it('falls back to Idle for unrecognized values', () => {
    expect(parseEpicStatus('not-a-status')).toBe(EpicStatus.Idle)
    expect(parseEpicStatus('')).toBe(EpicStatus.Idle)
  })
})
