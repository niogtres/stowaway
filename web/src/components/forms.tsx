import { X } from 'lucide-react'
import { useEffect, useRef, type ComponentProps, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger'

const variants: Record<Variant, string> = {
  primary: 'bg-accent text-bg hover:brightness-110 shadow-[0_8px_24px_-12px_var(--accent)]',
  secondary: 'glass text-fg hover:bg-accent-soft',
  ghost: 'text-muted hover:text-fg hover:bg-accent-soft',
  danger: 'bg-danger/15 text-danger hover:bg-danger/25',
}

export function Button({ variant = 'secondary', className, ...props }: ComponentProps<'button'> & { variant?: Variant }) {
  return (
    <button
      type="button"
      className={cn(
        'inline-flex items-center justify-center gap-2 rounded-xl px-3.5 py-2 text-sm font-medium transition disabled:pointer-events-none disabled:opacity-50',
        variants[variant],
        className,
      )}
      {...props}
    />
  )
}

export function Field({ label, hint, error, children }: { label: string; hint?: ReactNode; error?: string | null; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-xs font-medium text-muted">{label}</span>
      {children}
      {error ? <span className="mt-1 block text-xs text-danger">{error}</span> : hint ? <span className="mt-1 block text-xs text-muted">{hint}</span> : null}
    </label>
  )
}

export function TextInput({ className, ...props }: ComponentProps<'input'>) {
  return (
    <input
      className={cn(
        'w-full rounded-xl border border-line bg-surface-strong px-3 py-2 text-sm outline-none transition placeholder:text-muted focus:border-accent',
        className,
      )}
      {...props}
    />
  )
}

/** Accessible modal built on the native &lt;dialog&gt; element (focus trap and Escape for free). */
export function Dialog({
  open,
  onClose,
  title,
  description,
  children,
  footer,
  className,
}: {
  open: boolean
  onClose: () => void
  title: string
  description?: ReactNode
  children: ReactNode
  footer?: ReactNode
  className?: string
}) {
  const ref = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return
    if (open && !dialog.open) dialog.showModal()
    if (!open && dialog.open) dialog.close()
  }, [open])

  return (
    <dialog
      ref={ref}
      onClose={onClose}
      onClick={(e) => {
        if (e.target === ref.current) onClose()
      }}
      className={cn(
        'm-auto w-[min(34rem,calc(100vw-2rem))] rounded-2xl border border-line bg-surface-strong p-0 text-fg shadow-2xl backdrop-blur-2xl backdrop:bg-black/50 backdrop:backdrop-blur-sm',
        className,
      )}
    >
      {open && (
        <div className="p-6">
          <header className="mb-5 flex items-start justify-between gap-4">
            <div>
              <h2 className="text-lg font-semibold tracking-tight">{title}</h2>
              {description && <p className="mt-1 text-sm text-muted">{description}</p>}
            </div>
            <button type="button" onClick={onClose} className="rounded-lg p-1 text-muted transition hover:text-fg" aria-label="Close">
              <X className="size-4" />
            </button>
          </header>
          {children}
          {footer && <footer className="mt-6 flex justify-end gap-2">{footer}</footer>}
        </div>
      )}
    </dialog>
  )
}

export function ProgressBar({ percent, className }: { percent: number | null; className?: string }) {
  return (
    <div className={cn('h-1.5 overflow-hidden rounded-full bg-line', className)} role="progressbar" aria-valuenow={percent ?? undefined}>
      {percent === null ? (
        <div className="h-full w-1/3 animate-[indeterminate_1.4s_ease-in-out_infinite] rounded-full bg-accent" />
      ) : (
        <div className="h-full rounded-full bg-accent transition-[width] duration-500" style={{ width: `${Math.max(2, Math.min(100, percent))}%` }} />
      )}
    </div>
  )
}
