import { useSystemInfo } from '@/lib/api'
import { StatusDot } from './ui'

export function EngineStatusPill() {
  const { data, isError, isPending } = useSystemInfo()

  let tone: 'ok' | 'danger' | 'warn' | 'muted' = 'muted'
  let label = 'Connecting…'
  if (isError) {
    tone = 'danger'
    label = 'Service unreachable'
  } else if (data) {
    const engine = data.engine
    tone = engine.reachable ? 'ok' : 'warn'
    label = engine.reachable ? `${engine.operatingSystem ?? 'Docker'} ${engine.serverVersion ?? ''}`.trim() : 'Docker engine unavailable'
  }

  return (
    <div
      className="glass flex items-center gap-2 rounded-full px-3 py-1.5 text-xs font-medium"
      title={data?.engine.error ?? data?.engine.endpoint ?? undefined}
      aria-live="polite"
    >
      <StatusDot tone={tone} pulse={tone === 'ok' && !isPending} />
      <span className="max-w-56 truncate">{label}</span>
    </div>
  )
}
