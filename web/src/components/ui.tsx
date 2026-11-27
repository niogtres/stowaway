import type { ComponentProps, ReactNode } from 'react'
import { cn } from '@/lib/utils'

export function Card({ className, ...props }: ComponentProps<'section'>) {
  return <section className={cn('glass rounded-2xl p-5', className)} {...props} />
}

export function CardHeader({ title, icon, action }: { title: string; icon?: ReactNode; action?: ReactNode }) {
  return (
    <header className="mb-4 flex items-center justify-between gap-3">
      <h2 className="flex items-center gap-2 text-sm font-medium text-muted">
        {icon}
        {title}
      </h2>
      {action}
    </header>
  )
}

type Tone = 'ok' | 'warn' | 'danger' | 'muted' | 'accent'

const toneText: Record<Tone, string> = {
  ok: 'text-ok',
  warn: 'text-warn',
  danger: 'text-danger',
  muted: 'text-muted',
  accent: 'text-accent',
}

const toneBg: Record<Tone, string> = {
  ok: 'bg-ok',
  warn: 'bg-warn',
  danger: 'bg-danger',
  muted: 'bg-muted',
  accent: 'bg-accent',
}

export function StatusDot({ tone, pulse = false }: { tone: Tone; pulse?: boolean }) {
  return (
    <span className="relative inline-flex size-2.5">
      {pulse && <span className={cn('absolute inset-0 animate-ping rounded-full opacity-60', toneBg[tone])} />}
      <span className={cn('relative inline-flex size-2.5 rounded-full', toneBg[tone])} />
    </span>
  )
}

export function Badge({ tone = 'muted', children, className }: { tone?: Tone; children: ReactNode; className?: string }) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded-full border border-line px-2 py-0.5 text-[11px] font-medium tracking-wide',
        toneText[tone],
        className,
      )}
    >
      {children}
    </span>
  )
}

export function Stat({ label, value, hint }: { label: string; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="text-xs text-muted">{label}</div>
      <div className="mt-1 truncate text-2xl font-semibold tabular-nums tracking-tight">{value}</div>
      {hint && <div className="mt-0.5 truncate text-xs text-muted">{hint}</div>}
    </div>
  )
}

export function Skeleton({ className }: { className?: string }) {
  return <div className={cn('animate-pulse rounded-md bg-line', className)} />
}
