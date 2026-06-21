export type LabelOrigin = 'system' | 'user'

export interface LabelDefinition {
  key: string
  description: string
  origin: LabelOrigin
  supportedValues?: string[] | null
}

export interface LabelDefinitionInput {
  key: string
  description: string
  supportedValues?: string[] | null
}

export interface LabelDefinitionPatch {
  description?: string
  supportedValues?: string[] | null
}
