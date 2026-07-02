import * as React from "react"

import { cn } from "@/shared/lib/utils"

type FieldErrorProps = React.ComponentPropsWithoutRef<"p"> & {
  id?: string
}

function useFieldErrorId(id?: string) {
  const generatedId = React.useId()
  return id ?? `field-error-${generatedId}`
}

const FieldError = React.forwardRef<HTMLParagraphElement, FieldErrorProps>(
  function FieldError({ id, className, ...props }, ref) {
    const errorId = useFieldErrorId(id)

    return (
      <p
        ref={ref}
        id={errorId}
        role="alert"
        className={cn("text-xs text-red-700", className)}
        {...props}
      />
    )
  },
)

export { FieldError }
export { useFieldErrorId }
export type { FieldErrorProps }
