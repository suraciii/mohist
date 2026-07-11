type ScopedProperty = {
  target: object
  key: PropertyKey
  originalDescriptor: PropertyDescriptor | undefined
}

const scopedProperties: ScopedProperty[] = []

export function setScopedProperty(target: object, key: PropertyKey, descriptor: PropertyDescriptor): void {
  const originalDescriptor = Object.getOwnPropertyDescriptor(target, key)
  Object.defineProperty(target, key, descriptor)
  scopedProperties.push({ target, key, originalDescriptor })
}

export function setScopedValue(target: object, key: PropertyKey, value: unknown): void {
  setScopedProperty(target, key, {
    configurable: true,
    writable: true,
    value,
  })
}

export function restoreScopedProperties(): void {
  const errors: unknown[] = []

  while (scopedProperties.length > 0) {
    const scopedProperty = scopedProperties.pop()!
    try {
      if (scopedProperty.originalDescriptor) {
        Object.defineProperty(scopedProperty.target, scopedProperty.key, scopedProperty.originalDescriptor)
      } else if (!Reflect.deleteProperty(scopedProperty.target, scopedProperty.key)) {
        throw new Error(`Could not restore property ${String(scopedProperty.key)}`)
      }
    } catch (error) {
      errors.push(error)
    }
  }

  if (errors.length === 1) throw errors[0]
  if (errors.length > 1) throw new AggregateError(errors, 'Could not restore scoped properties')
}
