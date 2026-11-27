import { AnimatePresence, motion } from 'motion/react'
import { ChevronDown, Square } from 'lucide-react'
import { useMemo, useState } from 'react'
import activityIllustration from '@/assets/illustrations/empty-plans.webp'
import { ActiveRun, RunStatusIcon } from '@/components/backup'
import { Button } from '@/components/forms'
import { Badge, Card, Skeleton } from '@/components/ui'
import { useBackupProgress, useBackupRuns, useCancelBackup, useDestinations, type BackupRun } from '@/lib/api'
import { runStatusLabels } from '@/lib/backups'
import { cn, formatBytes, relativeTime } from '@/lib/utils'

function duration(run: BackupRun) {
  if (!run.startedAt) return '—'
  const end = run.finishedAt ? new Date(run.finishedAt).getTime() : Date.now()
  const seconds = Math.max(0, Math.round((end - new Date(run.startedAt).getTime()) / 1000))
  return seconds >= 60 ? `${Math.floor(seconds / 60)}m ${seconds % 60}s` : `${seconds}s`
}

export function Activity() {
  const runs = useBackupRuns()
  const destinations = useDestinations()
  const { data: progress } = useBackupProgress()
  const cancel = useCancelBackup()
  const [expanded, setExpanded] = useState<string | null>(null)
  const destinationNames = useMemo(() => new Map((destinations.data ?? []).map((d) => [d.id, d.name])), [destinations.data])

  if (runs.isPending) return <Skeleton className="mx-auto h-64 max-w-5xl rounded-2xl" />

  const list = runs.data ?? []
  if (list.length === 0) {
    return (
      <div className="mx-auto max-w-xl pt-10">
        <Card className="flex flex-col items-center gap-4 px-10 pb-12 pt-6 text-center">
          <img src={activityIllustration} alt="" width={320} height={240} className="h-60 w-80 select-none object-contain" draggable={false} />
          <h2 className="text-lg font-semibold tracking-tight">No backups yet</h2>
          <p className="text-sm text-muted">Start one from the Inventory with "Back up now". Every run shows up here with its full log.</p>
        </Card>
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-5xl">
      <Card className="p-0">
        <ul className="divide-y divide-line">
          {list.map((run) => {
            const active = run.status === 'running' || run.status === 'queued'
            const open = expanded === run.id
            return (
              <li key={run.id}>
                <div className="flex items-center gap-4 px-5 py-3.5">
                  <RunStatusIcon status={run.status} />
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-x-2">
                      <span className="font-medium">{run.appName}</span>
                      <span className="text-sm text-muted">→ {destinationNames.get(run.destinationId) ?? 'removed destination'}</span>
                      <Badge>{run.strategy === 'stopStart' ? 'Stop & start' : 'Live'}</Badge>
                    </div>
                    {active ? (
                      <div className="mt-2 max-w-md">
                        <ActiveRun run={run} progress={progress[run.id]} />
                      </div>
                    ) : (
                      <div className="mt-0.5 text-xs text-muted">
                        {runStatusLabels[run.status]} {relativeTime(run.finishedAt ?? run.queuedAt)} · took {duration(run)}
                        {run.bytesProcessed !== null && ` · ${formatBytes(run.bytesProcessed)} scanned, ${formatBytes(run.bytesAdded)} new`}
                        {run.downtimeSeconds !== null && ` · ${run.downtimeSeconds}s downtime`}
                      </div>
                    )}
                    {run.error && <div className="mt-1 text-xs text-danger">{run.error}</div>}
                  </div>
                  {active && (
                    <Button variant="ghost" className="text-xs" onClick={() => cancel.mutate(run.id)} disabled={cancel.isPending}>
                      <Square className="size-3.5" /> Cancel
                    </Button>
                  )}
                  <Button variant="ghost" onClick={() => setExpanded(open ? null : run.id)} aria-expanded={open} aria-label="Show log">
                    <ChevronDown className={cn('size-4 transition', open && 'rotate-180')} />
                  </Button>
                </div>
                <AnimatePresence initial={false}>
                  {open && (
                    <motion.div initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }} className="overflow-hidden">
                      <pre className="mx-5 mb-4 max-h-80 overflow-auto rounded-xl border border-line bg-bg/60 p-3 font-mono text-[11px] leading-relaxed text-muted">
                        {run.log || 'No log yet.'}
                        {run.snapshotId && `\n\nSnapshot ${run.snapshotId}`}
                      </pre>
                    </motion.div>
                  )}
                </AnimatePresence>
              </li>
            )
          })}
        </ul>
      </Card>
    </div>
  )
}
