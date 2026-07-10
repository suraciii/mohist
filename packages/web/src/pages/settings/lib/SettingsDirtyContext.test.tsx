// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { act, render, renderHook } from '@testing-library/react'
import { type ReactNode } from 'react'
import { describe, expect, it } from 'vitest'
import {
  SettingsDirtyProvider,
  useSettingsDirty,
} from './SettingsDirtyContext'

function DirtyStateControls() {
  const { dirty, setDirty } = useSettingsDirty()
  return (
    <div>
      <span data-testid="dirty">{String(dirty)}</span>
      <button type="button" data-testid="mark" onClick={() => setDirty(true)}>
        mark
      </button>
      <button type="button" data-testid="clear" onClick={() => setDirty(false)}>
        clear
      </button>
    </div>
  )
}

function ReadOnly() {
  const { dirty } = useSettingsDirty()
  return <span data-testid="other">{String(dirty)}</span>
}

describe('SettingsDirtyContext', () => {
  it('defaults to not dirty when no provider is present (graceful no-op)', () => {
    const { result } = renderHook(() => useSettingsDirty())
    expect(result.current.dirty).toBe(false)

    expect(() => result.current.setDirty(true)).not.toThrow()
  })

  it('publishes setDirty updates to all consumers', () => {
    function Wrapper({ children }: { children: ReactNode }) {
      return (
        <SettingsDirtyProvider>
          <DirtyStateControls />
          {children}
        </SettingsDirtyProvider>
      )
    }

    const { getByTestId } = render(
      <Wrapper>
        <ReadOnly />
      </Wrapper>,
    )

    expect(getByTestId('dirty').textContent).toBe('false')
    expect(getByTestId('other').textContent).toBe('false')

    act(() => {
      getByTestId('mark').click()
    })
    expect(getByTestId('dirty').textContent).toBe('true')
    expect(getByTestId('other').textContent).toBe('true')

    act(() => {
      getByTestId('clear').click()
    })
    expect(getByTestId('dirty').textContent).toBe('false')
    expect(getByTestId('other').textContent).toBe('false')
  })
})
