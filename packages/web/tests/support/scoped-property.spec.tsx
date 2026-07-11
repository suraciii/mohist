import { describe, expect, it } from 'vitest'
import { restoreScopedProperties, setScopedProperty, setScopedValue } from './scoped-property'

describe('scoped property support', () => {
  it('restores an own property descriptor', () => {
    const target: Record<string, unknown> = {}
    const originalDescriptor: PropertyDescriptor = {
      configurable: true,
      enumerable: false,
      writable: false,
      value: 'original',
    }
    Object.defineProperty(target, 'value', originalDescriptor)

    setScopedProperty(target, 'value', {
      configurable: true,
      enumerable: true,
      writable: true,
      value: 'scoped',
    })
    restoreScopedProperties()

    expect(Object.getOwnPropertyDescriptor(target, 'value')).toEqual(originalDescriptor)
  })

  it('removes a scoped override of an inherited property', () => {
    const target = Object.create({ value: 'inherited' }) as Record<string, unknown>

    setScopedValue(target, 'value', 'scoped')
    restoreScopedProperties()

    expect(Object.hasOwn(target, 'value')).toBe(false)
    expect(target.value).toBe('inherited')
  })

  it('removes a scoped absent property', () => {
    const target: Record<string, unknown> = {}

    setScopedValue(target, 'value', 'scoped')
    restoreScopedProperties()

    expect(Object.hasOwn(target, 'value')).toBe(false)
  })

  it('restores stacked writes in reverse order', () => {
    const target: Record<string, unknown> = {}

    setScopedValue(target, 'value', 'first')
    setScopedValue(target, 'value', 'second')
    restoreScopedProperties()

    expect(Object.hasOwn(target, 'value')).toBe(false)
  })
})
