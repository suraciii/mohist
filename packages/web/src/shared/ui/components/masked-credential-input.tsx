import * as React from "react"

import { Input } from "@/shared/ui/components/input"

export type MaskedCredentialInputProps = Omit<
  React.ComponentProps<"input">,
  "type"
>

const MaskedCredentialInput = React.forwardRef<
  HTMLInputElement,
  MaskedCredentialInputProps
>(function MaskedCredentialInput({ className, ...props }, ref) {
  return (
    <Input
      ref={ref}
      type="password"
      data-slot="masked-credential-input"
      autoComplete="off"
      autoCorrect="off"
      autoCapitalize="off"
      spellCheck={false}
      className={className}
      {...props}
    />
  )
})

export { MaskedCredentialInput }
