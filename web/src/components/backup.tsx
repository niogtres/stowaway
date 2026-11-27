import { Link } from '@tanstack/react-router'
import { CheckCircle2, CircleSlash, Clock, Loader2, PauseCircle, Play, XCircle, Zap } from 'lucide-react'
import { useState } from 'react'
import { Button, Dialog, ProgressBar } from '@/components/forms'
import {
  useBackupProgress,
  useBackupRuns,
  useDestinations,
  useStartBackup,
  type AppInfo,
  type BackupProgress,
  type BackupRun,
  type BackupStrategy,
  type RunStatus,
} from '@/lib/api'
import { cn, formatBytes, relativeTime } from '@/lib/utils'

function defaultStrategy(app: AppInfo): BackupStrategy {
  return app.containers.some((c) => c.database && c.state === 'running') ? 'stopStart' : 'live'
}

export function BackupNowDialog({ app, open, onClose }: { app: AppInfo; open: boolean; onClose: () => void }) {
  const destinations = useDestinations()
  const start = useStartBackup()
  const usable = (destinations.data ?? []).filter((d) => d.status?.reachable !== false)
  const [destinationId, setDestinationId] = useState<string | null>(null)
  const [strategy, setStrategy] = useState<BackupStrategy>(() => defaultStrategy(app))
  const selected = destinationId ?? usable[0]?.id ?? null
  const running = app.containers.filter((c) => c.state === 'running')
  const hasDatabase = app.containers.some((c) => c.database)

  const close = () => {
    start.reset()
    onClose()
  }

  return (
    <Dialog
      open={open}
      onClose={close}
      title={`Back up ${app.name}`}
      description={`${formatBytes(app.dataBytes, { lowerBound: app.sizeIncomplete })} of data. Only changes since the last backup are stored.`}
      footer={
        <>
          <Button onClick={close}>Cancel</Button>
          <Button
            variant="primary"
            disabled={!selected || start.isPending}
            onClick={() => selected && start.mutate({ appId: app.id, destinationId: selected, strategy }, { onSuccess: close })}
          >
            <Play className="size-4" /> Back up now
          </Button>
        </>
      }
    >
      {destinations.data && destinations.data.length === 0 ? (
        <p className="rounded-xl bg-accent-soft p-4 text-sm">
          Add a destination first.{' '}
          <Link to="/destinations" className="font-medium text-accent underline-offset-2 hover:underline">
            Go to Destinations
          </Link>
        </p>
      ) : (
        <div className="space-y-5">
          <fieldset>
            <legend className="mb-2 text-xs font-medium text-muted">Destination</legend>
            <div className="grid gap-2">
              {(destinations.data ?? []).map((d) => (
                <label
                  key={d.id}
                  className={cn(
                    'flex cursor-pointer items-center justify-between gap-3 rounded-xl border px-3 py-2 text-sm transition',
                    selected === d.id ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted',
                    d.status?.reachable === false && 'pointer-events-none opacity-50',
                  )}
                >
                  <span className="flex shrink-0 items-center gap-2">
                    <input type="radio" name="destination" className="accent-[var(--accent)]" checked={selected === d.id} onChange={() => setDestinationId(d.id)} />
                    <span className="whitespace-nowrap font-medium">{d.name}</span>
                  </span>
                  <span className="min-w-0 truncate font-mono text-xs text-muted" title={d.path}>
                    {d.path}
                  </span>
                </label>
              ))}
            </div>
          </fieldset>

          <fieldset>
            <legend className="mb-2 text-xs font-medium text-muted">Consistency</legend>
            <div className="grid grid-cols-2 gap-2">
              <StrategyOption
                active={strategy === 'live'}
                onSelect={() => setStrategy('live')}
                icon={Zap}
                title="Live"
                text="No downtime. Fine for config and files that rarely change mid-backup."
              />
              <StrategyOption
                active={strategy === 'stopStart'}
                onSelect={() => setStrategy('stopStart')}
                icon={PauseCircle}
                title="Stop & start"
                text={`Stops ${running.length} running container${running.length === 1 ? '' : 's'} briefly for a consistent copy.`}
              />
            </div>
            {hasDatabase && strategy === 'live' && (
              <p className="mt-2 text-xs text-warn">
                This app runs a database. A live copy of database files may not restore cleanly. Stop &amp; start is safer until
                dump-based backups arrive.
              </p>
            )}
          </fieldset>

          {start.error && <p className="text-sm text-danger">{start.error.message}</p>}
        </div>
      )}
    </Dialog>
  )
}

function StrategyOption({ active, onSelect, icon: Icon, title, text }: { active: boolean; onSelect: () => void; icon: typeof Zap; title: string; text: string }) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      onClick={onSelect}
      className={cn('rounded-xl border p-3 text-left transition', active ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted')}
    >
      <span className="flex items-center gap-2 text-sm font-medium">
        <Icon className={cn('size-4', active ? 'text-accent' : 'text-muted')} /> {title}
      </span>
      <span className="mt-1 block text-xs text-muted">{text}</span>
    </button>
  )
}

const statusMeta: Record<RunStatus, { icon: typeof Clock; tone: string; label: string }> = {
  queued: { icon: Clock, tone: 'text-muted', label: 'Queued' },
  running: { icon: Loader2, tone: 'text-accent', label: 'Running' },
  succeeded: { icon: CheckCircle2, tone: 'text-ok', label: 'Succeeded' },
  failed: { icon: XCircle, tone: 'text-danger', label: 'Failed' },
  cancelled: { icon: CircleSlash, tone: 'text-muted', label: 'Cancelled' },
}

export function RunStatusIcon({ status, className }: { status: RunStatus; className?: string }) {
  const { icon: Icon, tone, label } = statusMeta[status]
  return <Icon className={cn('size-4', tone, status === 'running' && 'animate-spin', className)} aria-label={label} />
}

function progressPercent(progress: BackupProgress | undefined) {
  if (!progress || progress.phase !== 'copying' || progress.percent === null) return null
  return progress.percent
}

/** Last backup and live progress for one app, shown on its inventory card. */
export function AppBackupStatus({ app, onBackup }: { app: AppInfo; onBackup: () => void }) {
  const runs = useBackupRuns()
  const { data: progress } = useBackupProgress()
  const appRuns = (runs.data ?? []).filter((r) => r.appId === app.id)
  const active = appRuns.find((r) => r.status === 'running' || r.status === 'queued')
  const lastDone = appRuns.find((r) => r.status === 'succeeded')
  const lastFailed = appRuns[0]?.status === 'failed' ? appRuns[0] : undefined

  return (
    <div className="mt-3 border-t border-line pt-3">
      {active ? (
        <ActiveRun run={active} progress={progress[active.id]} />
      ) : (
        <div className="flex items-center justify-between gap-3">
          <div className="min-w-0 text-xs text-muted">
            {lastFailed ? (
              <span className="text-danger" title={lastFailed.error ?? undefined}>
                Last backup failed {relativeTime(lastFailed.finishedAt)}
              </span>
            ) : lastDone ? (
              <>
                Last backup {relativeTime(lastDone.finishedAt)} · {formatBytes(lastDone.bytesAdded)} new
              </>
            ) : (
              'Never backed up'
            )}
          </div>
          <Button variant="secondary" className="px-3 py-1.5 text-xs" onClick={onBackup}>
            <Play className="size-3.5" /> Back up now
          </Button>
        </div>
      )}
    </div>
  )
}

export function ActiveRun({ run, progress }: { run: BackupRun; progress?: BackupProgress }) {
  const percent = progressPercent(progress)
  return (
    <div>
      <div className="mb-1.5 flex items-center justify-between text-xs">
        <span className="flex items-center gap-1.5">
          <RunStatusIcon status={run.status} className="size-3.5" />
          {run.status === 'queued' ? 'Waiting in queue…' : progress ? capitalize(progress.phase) : 'Starting…'}
        </span>
        <span className="tabular-nums text-muted">
          {percent !== null && progress ? `${formatBytes(progress.bytesDone)} of ${formatBytes(progress.totalBytes)} · ${percent.toFixed(0)}%` : ''}
        </span>
      </div>
      <ProgressBar percent={run.status === 'queued' ? 0 : percent} />
    </div>
  )
}

const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)
