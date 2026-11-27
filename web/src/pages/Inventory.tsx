import { AnimatePresence, motion } from 'motion/react'
import { Boxes, Container, Database, FolderTree, Layers, RefreshCw, Search, TriangleAlert } from 'lucide-react'
import { useDeferredValue, useMemo, useState } from 'react'
import emptyInventory from '@/assets/illustrations/empty-inventory.webp'
import engineOffline from '@/assets/illustrations/engine-offline.webp'
import { AppBackupStatus, BackupNowDialog } from '@/components/backup'
import { ContainerRow, MountRow, OutsideDataNote, ProtectionChip } from '@/components/inventory'
import { Badge, Card, Skeleton } from '@/components/ui'
import { useInventory, useRefreshInventory, type AppInfo, type InventorySnapshot, type MountInfo, type VolumeInfo } from '@/lib/api'
import { cn, formatBytes, formatNumber, shortVolumeName, timeAgo } from '@/lib/utils'

type Filter = 'all' | 'compose' | 'standalone' | 'databases' | 'attention'

const filters: { id: Filter; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'attention', label: 'Unprotected' },
  { id: 'databases', label: 'Databases' },
  { id: 'compose', label: 'Compose' },
  { id: 'standalone', label: 'Standalone' },
]

function matchesFilter(app: AppInfo, filter: Filter) {
  switch (filter) {
    case 'compose':
      return app.kind === 'compose'
    case 'standalone':
      return app.kind === 'standalone'
    case 'databases':
      return app.containers.some((c) => c.database)
    case 'attention':
      return app.protection === 'unprotected' || app.protection === 'failing' || app.protection === 'stale'
    default:
      return true
  }
}

function matchesSearch(app: AppInfo, term: string) {
  if (!term) return true
  const t = term.toLowerCase()
  return (
    app.name.toLowerCase().includes(t) ||
    app.containers.some(
      (c) =>
        c.name.toLowerCase().includes(t) ||
        c.image.toLowerCase().includes(t) ||
        c.mounts.some((m) => m.displaySource.toLowerCase().includes(t) || m.destination.toLowerCase().includes(t)),
    )
  )
}

export function Inventory() {
  const { data, isPending, isError } = useInventory()
  const refresh = useRefreshInventory()
  const [filter, setFilter] = useState<Filter>('all')
  const [search, setSearch] = useState('')
  const term = useDeferredValue(search.trim())

  const snapshot = data?.snapshot ?? null
  const apps = useMemo(
    () => (snapshot?.apps ?? []).filter((a) => matchesFilter(a, filter) && matchesSearch(a, term)),
    [snapshot, filter, term],
  )
  const orphans = useMemo(
    () => (snapshot?.volumes ?? []).filter((v) => v.orphaned && (!term || v.name.toLowerCase().includes(term.toLowerCase()))),
    [snapshot, term],
  )

  if (isPending) return <InventorySkeleton />

  if (isError || !snapshot) {
    const offline = isError || data?.engineReachable === false
    return (
      <EmptyState
        image={offline ? engineOffline : emptyInventory}
        title={offline ? 'Waiting for Docker' : 'Discovering…'}
        body={
          offline
            ? "The service can't reach the Docker engine yet. Inventory appears as soon as it's available."
            : 'Scanning containers, volumes and mounts. This only takes a moment.'
        }
      />
    )
  }

  return (
    <div className="mx-auto max-w-7xl">
      {!data?.engineReachable && (
        <div className="glass mb-4 flex items-center gap-2 rounded-xl px-4 py-2.5 text-sm text-warn">
          <TriangleAlert className="size-4" />
          Docker is unavailable: showing the last known inventory from {timeAgo(snapshot.capturedAt)}.
        </div>
      )}

      <SummaryStrip snapshot={snapshot} refreshing={refresh.isPending} onRefresh={() => refresh.mutate()} />

      <div className="mb-5 flex flex-wrap items-center gap-3">
        <div className="glass flex items-center gap-1 rounded-xl p-1" role="tablist" aria-label="Filter apps">
          {filters.map((f) => (
            <button
              key={f.id}
              type="button"
              role="tab"
              aria-selected={filter === f.id}
              onClick={() => setFilter(f.id)}
              className={cn(
                'rounded-lg px-3 py-1.5 text-xs font-medium text-muted transition hover:text-fg',
                filter === f.id && 'bg-accent-soft text-fg',
              )}
            >
              {f.label}
            </button>
          ))}
        </div>
        <label className="glass flex min-w-64 flex-1 items-center gap-2 rounded-xl px-3 py-2 sm:max-w-sm">
          <Search className="size-4 text-muted" />
          <input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search apps, images, paths…"
            className="w-full bg-transparent text-sm outline-none placeholder:text-muted"
            aria-label="Search inventory"
          />
        </label>
      </div>

      {apps.length === 0 ? (
        <p className="py-16 text-center text-sm text-muted">Nothing matches this filter.</p>
      ) : (
        <div className="grid gap-4 lg:grid-cols-2">
          <AnimatePresence initial={false}>
            {apps.map((app, i) => (
              <motion.div
                key={app.id}
                layout
                initial={{ opacity: 0, y: 8 }}
                animate={{ opacity: 1, y: 0, transition: { delay: Math.min(i, 12) * 0.025 } }}
                exit={{ opacity: 0 }}
              >
                <AppCard app={app} />
              </motion.div>
            ))}
          </AnimatePresence>
        </div>
      )}

      {orphans.length > 0 && (filter === 'all' || filter === 'attention') && <OrphanVolumes volumes={orphans} />}
    </div>
  )
}

function SummaryStrip({ snapshot, refreshing, onRefresh }: { snapshot: InventorySnapshot; refreshing: boolean; onRefresh: () => void }) {
  const t = snapshot.totals
  const items = [
    { icon: Boxes, label: 'Apps', value: formatNumber(t.apps) },
    { icon: Container, label: 'Containers', value: `${t.runningContainers}/${t.containers} running` },
    { icon: Layers, label: 'Volumes', value: formatNumber(t.volumes) },
    { icon: FolderTree, label: 'Bind mounts', value: formatNumber(t.bindMounts) },
    { icon: Database, label: 'Databases', value: formatNumber(t.databases) },
  ]
  return (
    <Card className="mb-5 flex flex-wrap items-center gap-x-8 gap-y-4 py-4">
      <div>
        <div className="text-xs text-muted">Data discovered</div>
        <div className="text-2xl font-semibold tabular-nums tracking-tight">{formatBytes(t.dataBytes)}</div>
      </div>
      {items.map(({ icon: Icon, label, value }) => (
        <div key={label} className="flex items-center gap-2.5">
          <Icon className="size-4 text-accent" />
          <div>
            <div className="text-xs text-muted">{label}</div>
            <div className="text-sm font-medium tabular-nums">{value}</div>
          </div>
        </div>
      ))}
      <div className="ml-auto flex items-center gap-3 text-xs text-muted">
        <span title={new Date(snapshot.capturedAt).toLocaleString()}>Updated {timeAgo(snapshot.capturedAt)}</span>
        <button
          type="button"
          onClick={onRefresh}
          disabled={refreshing}
          className="glass grid size-8 place-items-center rounded-full transition hover:text-fg disabled:opacity-60"
          aria-label="Rescan now"
          title="Rescan now"
        >
          <RefreshCw className={cn('size-4', refreshing && 'animate-spin')} />
        </button>
      </div>
    </Card>
  )
}

function AppCard({ app }: { app: AppInfo }) {
  const [backingUp, setBackingUp] = useState(false)
  // The same source mounted at the same path by several containers is one piece of data: show it once.
  const mounts = [
    ...app.containers
      .flatMap((c) => c.mounts)
      .reduce((byKey, m) => {
        const key = `${m.kind}:${m.displaySource}:${m.destination}`
        const existing = byKey.get(key)
        byKey.set(key, existing ? { ...existing, count: existing.count + 1 } : { ...m, key, count: 1 })
        return byKey
      }, new Map<string, MountInfo & { key: string; count: number }>())
      .values(),
  ]
  return (
    <Card className="flex h-full flex-col">
      <header className="mb-2 flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <h3 className="truncate text-base font-semibold tracking-tight">{app.name}</h3>
            <Badge>{app.kind === 'compose' ? 'Compose' : 'Standalone'}</Badge>
          </div>
          {app.composeWorkingDir && (
            <div className="mt-0.5 truncate font-mono text-[11px] text-muted" title={app.composeConfigFiles.join('\n')}>
              {app.composeWorkingDir}
            </div>
          )}
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1.5">
          <ProtectionChip status={app.protection} />
          {app.protection !== 'stateless' && (
            <span className="text-sm font-medium tabular-nums">{formatBytes(app.dataBytes, { lowerBound: app.sizeIncomplete })}</span>
          )}
        </div>
      </header>

      <div className="divide-y divide-line">
        {app.containers.map((c) => (
          <ContainerRow key={c.id} container={c} />
        ))}
      </div>

      {mounts.length > 0 && (
        <div className="mt-2 border-t border-line pt-2">
          {mounts.map((m) => (
            <MountRow key={m.key} mount={m} sharedBy={m.count} />
          ))}
          <OutsideDataNote mounts={mounts} />
        </div>
      )}

      <div className="mt-auto" />
      <AppBackupStatus app={app} onBackup={() => setBackingUp(true)} />
      <BackupNowDialog app={app} open={backingUp} onClose={() => setBackingUp(false)} />
    </Card>
  )
}

function OrphanVolumes({ volumes }: { volumes: VolumeInfo[] }) {
  const total = volumes.reduce((sum, v) => sum + (v.sizeBytes ?? 0), 0)
  return (
    <section className="mt-10">
      <div className="mb-3 flex items-end justify-between gap-4">
        <div>
          <h2 className="flex items-center gap-2 font-semibold tracking-tight">
            Unowned volumes <Badge tone="warn">{volumes.length}</Badge>
          </h2>
          <p className="mt-1 max-w-2xl text-sm text-muted">
            No container (running or stopped) uses these volumes. They're often left over from removed containers, but they can still hold
            the only copy of something important.
          </p>
        </div>
        <div className="text-right">
          <div className="text-xs text-muted">Total</div>
          <div className="font-medium tabular-nums">{formatBytes(total)}</div>
        </div>
      </div>
      <Card className="p-0">
        <table className="w-full text-sm">
          <thead className="text-left text-xs text-muted">
            <tr className="border-b border-line">
              <th className="px-5 py-2.5 font-medium">Volume</th>
              <th className="px-5 py-2.5 font-medium">Project</th>
              <th className="px-5 py-2.5 font-medium">Created</th>
              <th className="px-5 py-2.5 text-right font-medium">Size</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-line">
            {volumes.map((v) => (
              <tr key={v.name} className="transition hover:bg-accent-soft/40">
                <td className="px-5 py-2.5">
                  <span className="font-mono text-xs" title={v.name}>
                    {shortVolumeName(v.name, v.anonymous)}
                  </span>
                  {v.anonymous && <span className="ml-2 text-xs text-muted">anonymous</span>}
                </td>
                <td className="px-5 py-2.5 text-muted">{v.composeProject ?? '—'}</td>
                <td className="px-5 py-2.5 text-muted">{v.createdAt ? new Date(v.createdAt).toLocaleDateString() : '—'}</td>
                <td className="px-5 py-2.5 text-right tabular-nums">
                  {v.exclusionReason ? (
                    <span className="text-xs text-muted" title={v.exclusionReason}>
                      network share, not included
                    </span>
                  ) : (
                    formatBytes(v.sizeBytes)
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
    </section>
  )
}

function EmptyState({ image, title, body }: { image: string; title: string; body: string }) {
  return (
    <div className="mx-auto max-w-xl pt-10">
      <Card className="flex flex-col items-center gap-4 px-10 pb-12 pt-6 text-center">
        <img src={image} alt="" width={320} height={240} className="h-60 w-80 select-none object-contain" draggable={false} />
        <div>
          <h2 className="text-lg font-semibold tracking-tight">{title}</h2>
          <p className="mt-2 text-sm text-muted">{body}</p>
        </div>
      </Card>
    </div>
  )
}

function InventorySkeleton() {
  return (
    <div className="mx-auto max-w-7xl">
      <Skeleton className="mb-5 h-20 rounded-2xl" />
      <div className="grid gap-4 lg:grid-cols-2">
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} className="h-56 rounded-2xl" />
        ))}
      </div>
    </div>
  )
}
