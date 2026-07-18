import * as React from "react"

import { cn } from "@/shared/lib/utils"
import { Button } from "@/shared/ui/components/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogTitle,
} from "@/shared/ui/components/dialog"

type AlertDialogTone = "destructive" | "default"

interface AlertDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: React.ReactNode
  description?: React.ReactNode
  confirmLabel?: React.ReactNode
  cancelLabel?: React.ReactNode
  tone?: AlertDialogTone
  loading?: boolean
  onConfirm: () => void
  "data-testid"?: string
}

function AlertDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
  tone = "default",
  loading = false,
  onConfirm,
  "data-testid": testId = "alert-dialog",
}: AlertDialogProps) {
  const cancelButtonRef = React.useRef<HTMLButtonElement>(null)

  const handleConfirm = () => {
    if (loading) return
    onConfirm()
  }

  const handleOpenChange = (next: boolean) => {
    if (loading) return
    onOpenChange(next)
  }

  const confirmVariant = tone === "destructive" ? "destructive" : "default"
  const confirmClassName =
    tone === "destructive" ? "bg-red-600 text-white hover:bg-red-700" : undefined

  return (
    <Dialog open={open} onOpenChange={handleOpenChange}>
      <DialogContent
        showCloseButton={false}
        initialFocus={cancelButtonRef}
        data-testid={testId}
        data-tone={tone}
      >
        <DialogTitle>{title}</DialogTitle>
        {description != null && <DialogDescription>{description}</DialogDescription>}
        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button
            ref={cancelButtonRef}
            variant="outline"
            size="sm"
            onClick={() => handleOpenChange(false)}
            disabled={loading}
            data-testid={`${testId}-cancel`}
            type="button"
          >
            {cancelLabel}
          </Button>
          <Button
            size="sm"
            variant={confirmVariant}
            onClick={handleConfirm}
            disabled={loading}
            data-testid={`${testId}-confirm`}
            className={cn(confirmClassName)}
            type="button"
          >
            {loading ? "Working..." : confirmLabel}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}

export { AlertDialog }
export type { AlertDialogProps, AlertDialogTone }
