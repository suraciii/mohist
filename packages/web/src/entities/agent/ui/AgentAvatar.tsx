import { useEffect, useState } from 'react'
import { BotIcon } from 'lucide-react'

export interface AgentAvatarProps {
  agentName: string
  avatar?: string | null
  className: string
  iconClassName: string
  testId?: string
}

function isImageSource(value: string): boolean {
  if (/^data:image\//i.test(value)) return true

  try {
    const url = new URL(value)
    return url.protocol === 'http:' || url.protocol === 'https:'
  } catch {
    return false
  }
}

export function AgentAvatar({
  agentName,
  avatar,
  className,
  iconClassName,
  testId = 'agent-avatar',
}: AgentAvatarProps) {
  const [imageFailed, setImageFailed] = useState(false)
  const value = avatar?.trim() ?? ''
  const imageSource = isImageSource(value) ? value : null
  useEffect(() => {
    setImageFailed(false)
  }, [agentName, imageSource])
  const image = imageSource !== null && !imageFailed

  return (
    <div
      className={`flex aspect-square items-center justify-center overflow-hidden ${className}`}
      data-testid={testId}
      data-avatar-state={image ? 'image' : imageSource ? 'fallback' : value ? 'text' : 'fallback'}
    >
      {image ? (
        <img
          src={imageSource}
          alt={`${agentName} avatar`}
          className="size-full object-cover"
          onError={() => setImageFailed(true)}
          data-testid={`${testId}-image`}
        />
      ) : value && !isImageSource(value) ? (
        <span
          role="img"
          aria-label={`${agentName} avatar`}
          className="max-w-full truncate px-1 text-center text-sm leading-none"
          data-testid={`${testId}-text`}
        >
          {value}
        </span>
      ) : (
        <BotIcon className={iconClassName} aria-hidden="true" />
      )}
    </div>
  )
}
