import type { ComponentType, HTMLAttributes } from 'react'
import type { Components, ExtraProps } from 'react-markdown'

export type HeadingLevel = 1 | 2 | 3 | 4 | 5 | 6

const clampHeading = (level: number): HeadingLevel => {
  if (level <= 1) return 1
  if (level >= 6) return 6
  return level as HeadingLevel
}

export function remapHeadingLevel(original: HeadingLevel, base: HeadingLevel): HeadingLevel {
  return clampHeading(original + (base - 1))
}

type HeadingProps = HTMLAttributes<HTMLHeadingElement> & ExtraProps

function makeHeading(
  originalLevel: HeadingLevel,
  base: HeadingLevel,
): ComponentType<HeadingProps> {
  const targetLevel = remapHeadingLevel(originalLevel, base)

  const Heading = ({ children, node: _node, ...props }: HeadingProps) => {
    const Tag = `h${targetLevel}` as `h${HeadingLevel}`

    return (
      <Tag data-heading-level={targetLevel} data-original-level={originalLevel} {...props}>
        {children}
      </Tag>
    )
  }

  return Heading
}

export function buildHeadingOverrides(options: { base: HeadingLevel }): Pick<Components, 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'h6'> {
  return {
    h1: makeHeading(1, options.base),
    h2: makeHeading(2, options.base),
    h3: makeHeading(3, options.base),
    h4: makeHeading(4, options.base),
    h5: makeHeading(5, options.base),
    h6: makeHeading(6, options.base),
  }
}
