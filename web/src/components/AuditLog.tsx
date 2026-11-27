import { Globe, Monitor } from 'lucide-react'
import { useAudit } from '@/lib/api'
import { cn } from '@/lib/utils'
import { Card, Skeleton } from './ui'

/** Settings → Audit log: sign-in events and every change made through DockerBackup, newest first. */
export function AuditLogSection() {
  const audit = useAudit()

  return (
    <section>
      <div className="mb-3">
        <h2 className="font-semibold tracking-tight">Audit log</h2>
        <p className="mt-1 text-sm text-muted">Sign-ins from other computers and every change made through DockerBackup, kept for 180 days.</p>
      </div>
      {audit.isPending ? (
        <Skeleton className="h-32 rounded-2xl" />
      ) : (audit.data ?? []).length === 0 ? (
        <Card className="py-8 text-center text-sm text-muted">Nothing recorded yet.</Card>
      ) : (
        <Card className="max-h-[28rem] overflow-y-auto p-0">
          <table className="w-full text-left text-xs">
            <thead className="sticky top-0 bg-surface-strong text-muted">
              <tr>
                <th className="px-4 py-2 font-medium">When</th>
                <th className="px-4 py-2 font-medium">Who</th>
                <th className="px-4 py-2 font-medium">From</th>
                <th className="px-4 py-2 font-medium">What</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {audit.data!.map((e) => {
                const failed = (e.status !== null && e.status >= 400) || /fail|lock|refused/i.test(e.action)
                return (
                  <tr key={e.id}>
                    <td className="whitespace-nowrap px-4 py-2 text-muted">{new Date(e.at).toLocaleString()}</td>
                    <td className="px-4 py-2">{e.actor || '—'}</td>
                    <td className="whitespace-nowrap px-4 py-2 font-mono">
                      <span className="inline-flex items-center gap-1.5" title={e.remote ? 'Another computer' : 'This computer'}>
                        {e.remote ? <Globe className="size-3 text-muted" /> : <Monitor className="size-3 text-muted" />}
                        {e.address}
                      </span>
                    </td>
                    <td className={cn('px-4 py-2', failed && 'text-danger')}>
                      <span className="font-mono">{e.action}</span>
                      {e.status !== null && <span className="text-muted"> · {e.status}</span>}
                      {e.detail && <span className="text-muted"> · {e.detail}</span>}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </Card>
      )}
    </section>
  )
}
