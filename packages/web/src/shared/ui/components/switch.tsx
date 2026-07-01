import * as React from "react"
import { Switch as SwitchPrimitive } from "@base-ui/react/switch"

import { cn } from "@/shared/lib/utils"

function Switch({
  className,
  "aria-label": ariaLabel,
  ...props
}: React.ComponentPropsWithoutRef<typeof SwitchPrimitive.Root> & {
  "aria-label"?: string
}) {
  return (
    <SwitchPrimitive.Root
      data-slot="switch"
      aria-label={ariaLabel}
      className={cn(
        "peer inline-flex h-5 w-9 shrink-0 cursor-pointer items-center rounded-full border-2 border-transparent transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-50 data-checked:bg-primary data-unchecked:bg-input",
        className,
      )}
      {...props}
    >
      <SwitchPrimitive.Thumb
        data-slot="switch-thumb"
        className={cn(
          "pointer-events-none block size-4 rounded-full bg-background shadow-lg ring-0 transition-transform data-checked:translate-x-4 data-unchecked:translate-x-0",
        )}
      />
    </SwitchPrimitive.Root>
  )
}

export { Switch }
