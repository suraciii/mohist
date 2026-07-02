import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useState,
  type ReactNode,
} from 'react'

interface SettingsDirtyContextValue {
  dirty: boolean
  setDirty: (next: boolean) => void
}

const defaultValue: SettingsDirtyContextValue = {
  dirty: false,
  setDirty: () => undefined,
}

const SettingsDirtyContext = createContext<SettingsDirtyContextValue>(defaultValue)

export function SettingsDirtyProvider({ children }: { children: ReactNode }) {
  const [dirty, setDirtyState] = useState(false)

  const setDirty = useCallback((next: boolean) => {
    setDirtyState(next)
  }, [])

  const value = useMemo<SettingsDirtyContextValue>(
    () => ({ dirty, setDirty }),
    [dirty, setDirty],
  )

  return (
    <SettingsDirtyContext.Provider value={value}>
      {children}
    </SettingsDirtyContext.Provider>
  )
}

export function useSettingsDirty(): SettingsDirtyContextValue {
  return useContext(SettingsDirtyContext)
}
