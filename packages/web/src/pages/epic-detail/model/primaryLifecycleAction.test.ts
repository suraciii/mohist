import { describe, expect, it } from 'vitest'
import { EpicStatus } from '../../../entities/epic/model/types'
import { primaryLifecycleAction } from './primaryLifecycleAction'

describe('primaryLifecycleAction', () => {
  describe('terminal statuses (done, closed) always return reopen-epic', () => {
    it.each([
      [EpicStatus.Done, false],
      [EpicStatus.Done, true],
      [EpicStatus.Closed, false],
      [EpicStatus.Closed, true],
    ] as const)('returns reopen-epic for status=%s, readyToMarkDone=%s', (status, ready) => {
      expect(primaryLifecycleAction(status, ready)).toEqual({ kind: 'reopen-epic' })
    })
  })

  describe('idle status', () => {
    it('returns start-epic when not ready to mark done', () => {
      expect(primaryLifecycleAction(EpicStatus.Idle, false)).toEqual({ kind: 'start-epic' })
    })

    it('returns mark-done when ready to mark done (replacing start-epic)', () => {
      expect(primaryLifecycleAction(EpicStatus.Idle, true)).toEqual({ kind: 'mark-done' })
    })
  })

  describe('running status', () => {
    it('returns pause-epic when not ready to mark done', () => {
      expect(primaryLifecycleAction(EpicStatus.Running, false)).toEqual({ kind: 'pause-epic' })
    })

    it('returns mark-done when ready to mark done (replacing pause-epic)', () => {
      expect(primaryLifecycleAction(EpicStatus.Running, true)).toEqual({ kind: 'mark-done' })
    })
  })

  describe('paused status (always resume, never mark-done)', () => {
    it('returns resume-epic when not ready to mark done', () => {
      expect(primaryLifecycleAction(EpicStatus.Paused, false)).toEqual({ kind: 'resume-epic' })
    })

    it('returns resume-epic when ready to mark done (mark done is not surfaced as primary)', () => {
      expect(primaryLifecycleAction(EpicStatus.Paused, true)).toEqual({ kind: 'resume-epic' })
    })
  })

  describe('covers every cell of the (status × readyToMarkDone) matrix', () => {
    const allStatuses = [
      EpicStatus.Idle,
      EpicStatus.Running,
      EpicStatus.Paused,
      EpicStatus.Done,
      EpicStatus.Closed,
    ]
    const readyValues = [false, true]

    it.each(
      allStatuses.flatMap(status => readyValues.map(ready => [status, ready] as const)),
    )('returns a valid primary kind for status=%s, ready=%s', (status, ready) => {
      const action = primaryLifecycleAction(status, ready)
      if (status === EpicStatus.Done || status === EpicStatus.Closed) {
        expect(action).toEqual({ kind: 'reopen-epic' })
        return
      }
      expect(action?.kind).toMatch(/^(start-epic|pause-epic|resume-epic|mark-done)$/)
    })

    it('encodes the spec matrix exactly', () => {
      const expected: Record<string, string> = {
        'idle|false': 'start-epic',
        'idle|true': 'mark-done',
        'running|false': 'pause-epic',
        'running|true': 'mark-done',
        'paused|false': 'resume-epic',
        'paused|true': 'resume-epic',
        'done|false': 'reopen-epic',
        'done|true': 'reopen-epic',
        'closed|false': 'reopen-epic',
        'closed|true': 'reopen-epic',
      }
      for (const status of allStatuses) {
        for (const ready of readyValues) {
          const action = primaryLifecycleAction(status, ready)
          const got = action?.kind ?? 'none'
          expect(got, `status=${status} ready=${ready}`).toBe(expected[`${status}|${ready}`])
        }
      }
    })
  })
})
