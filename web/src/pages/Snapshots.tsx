import { ChevronRight, File, Folder, FolderOpen, HardDrive, Layers, RotateCcw, Search, ShieldCheck } from 'lucide-react'
import { useMemo, useState } from 'react'
import emptySnapshots from '@/assets/illustrations/empty-snapshots.webp'
import { RestoreDialog, type RestoreScope } from '@/components/RestoreDialog'
import { Button } from '@/components/forms'
import { Badge, Card, Skeleton } from '@/components/ui'
import {
  useDestinations,
  useInventory,
  usePlans,
  useSnapshotContents,
  useSnapshotListing,
  useSnapshots,
  type SnapshotInfo,
  type SnapshotSource,
} from '@/lib/api'
import { cn, formatBytes, relativeTime } from '@/lib/utils'

const itemLabel = (id: string) => id.replace(/^(compose|container):/, '').replace(/^volume:/, 'volume ')

export function Snapshots({ initialApp }: { initialApp?: string }) {
  const snapshots = useSnapshots()
  const inventory = useInventory()
  const [selectedApp, setSelectedApp] = useState<string | undefined>(initialApp)
  const [selectedSnapshot, setSelectedSnapshot] = useState<string | undefined>()
  const [term, setTerm] = useState('')

  const names = useMemo(() => new Map((inventory.data?.snapshot?.apps ?? []).map((a) => [a.id, a.name])), [inventory.data])
  const apps = useMemo(() => {
    const groups = new Map<string, SnapshotInfo[]>()
    for (const s of snapshots.data ?? []) {
      if (!s.appId) continue
      groups.set(s.appId, [...(groups.get(s.appId) ?? []), s])
    }
    return [...groups.entries()]
      .map(([id, list]) => ({ id, name: names.get(id) ?? itemLabel(id), exists: names.has(id) || id.startsWith('volume:'), snapshots: list }))
      .filter((a) => a.name.toLowerCase().includes(term.toLowerCase()))
      .sort((a, b) => a.name.localeCompare(b.name))
  }, [snapshots.data, names, term])

  if (snapshots.isPending) return <Skeleton className="mx-auto h-96 max-w-7xl rounded-2xl" />

  if ((snapshots.data ?? []).length === 0) {
    return (
      <div className="mx-auto max-w-xl pt-6">
        <Card className="flex flex-col items-center gap-4 px-10 pb-12 pt-6 text-center">
          <img src={emptySnapshots} alt="" width={320} height={240} className="h-60 w-80 select-none object-contain" draggable={false} />
          <h2 className="text-lg font-semibold tracking-tight">No snapshots yet</h2>
          <p className="text-sm text-muted">Once a backup has run, every point in time shows up here, ready to browse and restore.</p>
        </Card>
      </div>
    )
  }

  const app = apps.find((a) => a.id === selectedApp) ?? apps[0]
  // Default to the newest regular backup; safety snapshots are undo points, rarely what you want first.
  const snapshot = app?.snapshots.find((s) => s.id === selectedSnapshot) ?? app?.snapshots.find((s) => !s.safety) ?? app?.snapshots[0]

  return (
    <div className="mx-auto grid max-w-7xl gap-4 lg:grid-cols-[15rem_18rem_minmax(0,1fr)]">
      <Card className="p-2">
        <label className="mb-1 flex items-center gap-2 px-2 py-1.5">
          <Search className="size-4 text-muted" />
          <input value={term} onChange={(e) => setTerm(e.target.value)} placeholder="Filter" className="w-full bg-transparent text-sm outline-none placeholder:text-muted" />
        </label>
        <ul className="space-y-0.5">
          {apps.map((a) => (
            <li key={a.id}>
              <button
                type="button"
                onClick={() => {
                  setSelectedApp(a.id)
                  setSelectedSnapshot(undefined)
                }}
                className={cn('w-full rounded-xl px-3 py-2 text-left transition hover:bg-accent-soft', a.id === app?.id && 'bg-accent-soft')}
              >
                <span className="flex items-center justify-between gap-2">
                  <span className="truncate text-sm font-medium">{a.name}</span>
                  <span className="text-xs tabular-nums text-muted">{a.snapshots.length}</span>
                </span>
                <span className="block text-xs text-muted">
                  {relativeTime(a.snapshots[0].time)}
                  {!a.exists && ' · no longer running'}
                </span>
              </button>
            </li>
          ))}
        </ul>
      </Card>

      {app && <Timeline snapshots={app.snapshots} selected={snapshot?.id} onSelect={setSelectedSnapshot} />}
      {app && snapshot && <SnapshotDetail key={snapshot.id} snapshot={snapshot} appName={app.name} />}
    </div>
  )
}

function Timeline({ snapshots, selected, onSelect }: { snapshots: SnapshotInfo[]; selected?: string; onSelect: (id: string) => void }) {
  const destinations = useDestinations()
  const plans = usePlans()
  const destName = (id: string) => destinations.data?.find((d) => d.id === id)?.name ?? 'destination'
  const planName = (id: string | null) => (id ? plans.data?.find((p) => p.id === id)?.name ?? 'Plan' : null)
  const byDay = new Map<string, SnapshotInfo[]>()
  for (const s of snapshots) {
    const day = new Date(s.time).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })
    byDay.set(day, [...(byDay.get(day) ?? []), s])
  }

  return (
    <Card className="max-h-[calc(100vh-9rem)] overflow-y-auto p-3">
      {[...byDay.entries()].map(([day, list]) => (
        <section key={day} className="mb-3">
          <h3 className="sticky top-0 z-10 mb-1 bg-surface-strong px-2 py-1 text-xs font-medium text-muted backdrop-blur">{day}</h3>
          <ol className="relative ml-3 border-l border-line">
            {list.map((s) => (
              <li key={s.id}>
                <button
                  type="button"
                  onClick={() => onSelect(s.id)}
                  className={cn('relative w-full rounded-xl py-2 pl-5 pr-2 text-left transition hover:bg-accent-soft', s.id === selected && 'bg-accent-soft')}
                >
                  <span className={cn('absolute -left-[5px] top-3.5 size-2.5 rounded-full border-2 border-bg', s.safety ? 'bg-warn' : s.id === selected ? 'bg-accent' : 'bg-muted')} />
                  <span className="flex items-center justify-between gap-2 text-sm">
                    <span className="font-medium tabular-nums">{new Date(s.time).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}</span>
                    <span className="text-xs tabular-nums text-muted">{formatBytes(s.totalBytes)}</span>
                  </span>
                  <span className="mt-0.5 flex flex-wrap items-center gap-1 text-xs text-muted">
                    {s.safety ? <Badge tone="warn">Safety</Badge> : planName(s.planId) ? <Badge>{planName(s.planId)}</Badge> : <Badge>Manual</Badge>}
                    <span>{destName(s.destinationId)}</span>
                  </span>
                </button>
              </li>
            ))}
          </ol>
        </section>
      ))}
    </Card>
  )
}

function SnapshotDetail({ snapshot, appName }: { snapshot: SnapshotInfo; appName: string }) {
  const destinations = useDestinations()
  const contents = useSnapshotContents(snapshot.destinationId, snapshot.id)
  const [browsing, setBrowsing] = useState<SnapshotSource | null>(null)
  const [restore, setRestore] = useState<RestoreScope | null>(null)
  const destination = destinations.data?.find((d) => d.id === snapshot.destinationId)
  const sources = contents.data?.sources ?? []

  return (
    <Card className="min-w-0">
      <header className="mb-5 flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-lg font-semibold tracking-tight">{new Date(snapshot.time).toLocaleString(undefined, { dateStyle: 'full', timeStyle: 'short' })}</h2>
          <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
            <span>{relativeTime(snapshot.time)}</span>
            <span className="flex items-center gap-1">
              <HardDrive className="size-3.5" /> {destination?.name}
            </span>
            {snapshot.host && <span>from {snapshot.host}</span>}
            <span className="font-mono">{snapshot.shortId}</span>
            {snapshot.dataAdded !== null && <span>{formatBytes(snapshot.dataAdded)} new when taken</span>}
          </div>
          {snapshot.safety && (
            <p className="mt-2 flex items-center gap-1.5 text-xs text-warn">
              <ShieldCheck className="size-3.5" /> Safety snapshot taken just before a restore: restore it to undo that restore.
            </p>
          )}
        </div>
        <Button variant="primary" disabled={sources.length === 0} onClick={() => setRestore({ sources, subPath: null, label: `everything in ${appName}` })}>
          <RotateCcw className="size-4" /> Restore everything
        </Button>
      </header>

      {contents.isPending ? (
        <Skeleton className="h-32" />
      ) : contents.error ? (
        <p className="text-sm text-danger">{contents.error.message}</p>
      ) : (
        <ul className="divide-y divide-line rounded-xl border border-line">
          {sources.map((s) => (
            <li key={s.snapshotPath} className="flex items-center gap-3 px-4 py-3">
              {s.kind === 'volume' ? <Layers className="size-4 shrink-0 text-accent" /> : <FolderOpen className="size-4 shrink-0 text-accent" />}
              <div className="min-w-0 flex-1">
                <div className="truncate font-mono text-sm" title={s.displaySource}>
                  {s.displaySource}
                </div>
                <div className="text-xs text-muted">
                  {s.containerName} → {s.destination}
                  {s.sizeBytes !== null && ` · ${formatBytes(s.sizeBytes)}`}
                </div>
              </div>
              <Button variant="ghost" className="text-xs" onClick={() => setBrowsing(browsing?.snapshotPath === s.snapshotPath ? null : s)} aria-expanded={browsing?.snapshotPath === s.snapshotPath}>
                <Folder className="size-3.5" /> Browse
              </Button>
              <Button variant="secondary" className="text-xs" onClick={() => setRestore({ sources: [s], subPath: null, label: s.displaySource })}>
                <RotateCcw className="size-3.5" /> Restore…
              </Button>
            </li>
          ))}
          {sources.length === 0 && <li className="px-4 py-3 text-sm text-muted">This snapshot only holds the app definition.</li>}
        </ul>
      )}

      {browsing && (
        <FileBrowser
          key={browsing.snapshotPath}
          snapshot={snapshot}
          source={browsing}
          onRestore={(subPath, name) => setRestore({ sources: [browsing], subPath, label: name })}
        />
      )}

      {restore && <RestoreDialog snapshot={snapshot} appName={appName} scope={restore} onClose={() => setRestore(null)} />}
    </Card>
  )
}

function FileBrowser({ snapshot, source, onRestore }: { snapshot: SnapshotInfo; source: SnapshotSource; onRestore: (subPath: string, name: string) => void }) {
  const [relative, setRelative] = useState('')
  const path = source.snapshotPath + relative
  const listing = useSnapshotListing(snapshot.destinationId, snapshot.id, path)
  const crumbs = relative.split('/').filter(Boolean)

  return (
    <div className="mt-4 rounded-xl border border-line">
      <nav className="flex flex-wrap items-center gap-1 border-b border-line px-4 py-2 text-sm" aria-label="Path">
        <button type="button" onClick={() => setRelative('')} className="font-mono text-accent hover:underline">
          {source.destination}
        </button>
        {crumbs.map((c, i) => (
          <span key={i} className="flex items-center gap-1">
            <ChevronRight className="size-3.5 text-muted" />
            <button type="button" onClick={() => setRelative('/' + crumbs.slice(0, i + 1).join('/'))} className="font-mono hover:underline">
              {c}
            </button>
          </span>
        ))}
      </nav>
      {listing.isPending ? (
        <Skeleton className="m-4 h-20" />
      ) : listing.error ? (
        <p className="p-4 text-sm text-danger">{listing.error.message}</p>
      ) : (
        <ul className="max-h-80 overflow-y-auto">
          {(listing.data ?? []).length === 0 && <li className="px-4 py-3 text-sm text-muted">Empty folder.</li>}
          {(listing.data ?? []).map((e) => {
            const rel = e.path.slice(source.snapshotPath.length)
            return (
              <li key={e.path} className="group flex items-center gap-3 px-4 py-1.5 text-sm hover:bg-accent-soft/50">
                {e.type === 'dir' ? <Folder className="size-4 text-accent" /> : <File className="size-4 text-muted" />}
                {e.type === 'dir' ? (
                  <button type="button" onClick={() => setRelative(rel)} className="min-w-0 flex-1 truncate text-left hover:underline">
                    {e.name}
                  </button>
                ) : (
                  <span className="min-w-0 flex-1 truncate">{e.name}</span>
                )}
                <span className="w-20 text-right text-xs tabular-nums text-muted">{e.type === 'dir' ? '' : formatBytes(e.size)}</span>
                <span className="hidden w-32 text-right text-xs text-muted md:block">{e.modifiedAt ? new Date(e.modifiedAt).toLocaleDateString() : ''}</span>
                <Button variant="ghost" className="px-2 py-1 text-xs opacity-0 transition group-hover:opacity-100 focus:opacity-100" onClick={() => onRestore(rel, e.name)}>
                  <RotateCcw className="size-3.5" /> Restore
                </Button>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
