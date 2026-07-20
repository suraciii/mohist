import { act } from '@testing-library/react'
import { vi } from 'vitest'

interface IntersectionObserverRecord {
  callback: IntersectionObserverCallback
  options: IntersectionObserverInit
  observer: IntersectionObserverStub
}

class IntersectionObserverStub {
  readonly root: Element | Document | null
  readonly rootMargin: string
  readonly thresholds: readonly number[]
  readonly observedTargets: Element[] = []
  disconnected = false

  constructor(
    callback: IntersectionObserverCallback,
    options: IntersectionObserverInit,
    records: IntersectionObserverRecord[],
  ) {
    this.root = options.root ?? null
    this.rootMargin = options.rootMargin ?? '0px'
    this.thresholds = Array.isArray(options.threshold)
      ? options.threshold
      : [options.threshold ?? 0]
    records.push({ callback, options, observer: this })
  }

  observe(target: Element) {
    this.observedTargets.push(target)
  }

  unobserve(target: Element) {
    const index = this.observedTargets.indexOf(target)
    if (index >= 0) this.observedTargets.splice(index, 1)
  }

  disconnect() {
    this.disconnected = true
    this.observedTargets.length = 0
  }

  takeRecords(): IntersectionObserverEntry[] {
    return []
  }
}

export function installIntersectionObserver() {
  const records: IntersectionObserverRecord[] = []
  vi.stubGlobal('IntersectionObserver', class extends IntersectionObserverStub {
    constructor(callback: IntersectionObserverCallback, options: IntersectionObserverInit = {}) {
      super(callback, options, records)
    }
  })

  function getRecord() {
    const record = records[0]
    if (!record) throw new Error('IntersectionObserver was not registered')
    return record
  }

  return {
    getRecord,
    report(target: Element, intersectionRatio: number) {
      const record = getRecord()
      act(() => {
        record.callback(
          [{ target, intersectionRatio } as IntersectionObserverEntry],
          record.observer as unknown as IntersectionObserver,
        )
      })
    },
  }
}
