import { Ban, Boxes, Database, FolderOpen, HardDrive, Info, Layers, PackageOpen } from 'lucide-react'
import { databaseLabels, useIncludeFolder, type AppProduct, type ContainerInfo, type MountInfo, type ProtectionStatus } from '@/lib/api'
import { cn, formatBytes, shortVolumeName } from '@/lib/utils'
import { DatabaseLoginButton } from './DatabaseLogin'
import { Badge, StatusDot } from './ui'

const protectionLabels: Record<ProtectionStatus, string> = {
  protected: 'Protected',
  stale: 'Stale',
  failing: 'Failing',
  pending: 'Scheduled',
  unprotected: 'Unprotected',
  excluded: 'Excluded',
  stateless: 'Stateless',
}

const protectionTones: Record<ProtectionStatus, 'ok' | 'warn' | 'danger' | 'muted' | 'accent'> = {
  protected: 'ok',
  stale: 'warn',
  failing: 'danger',
  pending: 'accent',
  unprotected: 'warn',
  excluded: 'muted',
  stateless: 'muted',
}

const protectionHints: Record<ProtectionStatus, string> = {
  protected: 'Covered by a healthy plan',
  stale: 'Covered, but the last successful backup is overdue',
  failing: 'Covered, but the last scheduled backup failed',
  pending: 'Covered by a new plan that has not run yet',
  unprotected: 'No backup plan covers this data yet',
  excluded: 'Deliberately not backed up',
  stateless: 'Holds no persistent data: nothing to back up',
}

export function ProtectionChip({ status }: { status: ProtectionStatus }) {
  const tone = protectionTones[status]
  return (
    <span title={protectionHints[status]}>
      <Badge tone={tone}>
        <StatusDot tone={tone} />
        {protectionLabels[status]}
      </Badge>
    </span>
  )
}

function containerTone(c: ContainerInfo): 'ok' | 'warn' | 'danger' | 'muted' {
  if (c.health === 'unhealthy' || c.state === 'dead') return 'danger'
  if (c.state === 'restarting' || c.state === 'paused' || c.health === 'starting') return 'warn'
  if (c.state === 'running') return 'ok'
  return 'muted'
}

export function ContainerRow({ container }: { container: ContainerInfo }) {
  const tone = containerTone(container)
  return (
    <div className="flex items-start gap-3 py-2">
      <span className="mt-1.5" title={`${container.state}${container.health ? ` · ${container.health}` : ''}`}>
        <StatusDot tone={tone} />
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className="font-medium">{container.name}</span>
          {container.product && <ProductBadge product={container.product} />}
          {container.database && (
            <Badge tone="accent">
              <Database className="size-3" />
              {databaseLabels[container.database]}
            </Badge>
          )}
          {container.imageIsLocalOnly && (
            <span title="Built locally, so it can't be pulled from a registry after a disaster. Image backup is recommended.">
              <Badge tone="warn">
                <PackageOpen className="size-3" />
                Local image
              </Badge>
            </span>
          )}
          {container.health === 'unhealthy' && <Badge tone="danger">Unhealthy</Badge>}
          <DatabaseLoginButton container={container} />
        </div>
        <div className="mt-0.5 truncate font-mono text-[11px] text-muted" title={container.image}>
          {container.image} · {container.statusText}
        </div>
      </div>
    </div>
  )
}

/** e.g. "Business Central 13.18 · DK"; the full version in the tooltip. */
function ProductBadge({ product }: { product: AppProduct }) {
  const short = product.version?.split('.').slice(0, 2).join('.')
  const label = [product.name, short].filter(Boolean).join(' ') + (product.country ? ` · ${product.country}` : '')
  return (
    <span title={[product.name, product.version, product.country].filter(Boolean).join(' ')}>
      <Badge tone="accent">
        <Boxes className="size-3" />
        {label}
      </Badge>
    </span>
  )
}

const mountIcons = { volume: Layers, bind: FolderOpen, tmpfs: HardDrive, npipe: HardDrive, other: HardDrive }

export function MountRow({ mount, sharedBy = 1 }: { mount: MountInfo; sharedBy?: number }) {
  const Icon = mount.included ? mountIcons[mount.kind] : Ban
  const source = mount.kind === 'volume' ? shortVolumeName(mount.displaySource, mount.anonymous) : mount.displaySource
  return (
    <div
      className={cn('grid grid-cols-[1rem_minmax(0,1fr)_auto] items-center gap-x-2.5 py-1 text-xs', !mount.included && 'opacity-55')}
      title={mount.exclusionReason ?? mount.note ?? `${mount.kind} · ${mount.displaySource} → ${mount.destination}${mount.readOnly ? ' (read-only)' : ''}`}
    >
      <Icon className={cn('size-3.5', mount.included ? 'text-accent' : 'text-muted')} />
      <div className="min-w-0 truncate">
        <span className="font-mono">{source}</span>
        {mount.anonymous && <span className="ml-1.5 text-muted">anonymous</span>}
        <span className="mx-1.5 text-muted">→</span>
        <span className="font-mono text-muted">{mount.destination}</span>
        {sharedBy > 1 && (
          <span className="ml-1.5 text-muted" title={`Mounted by ${sharedBy} containers`}>
            ×{sharedBy}
          </span>
        )}
      </div>
      <span className="flex items-center gap-2 tabular-nums text-muted">
        {mount.includedByOwner && <span className="text-accent">included by you</span>}
        {mount.note && (
          <span className="text-accent" title={mount.note}>
            {mount.note.split(':')[0]}
          </span>
        )}
        {mount.included ? formatBytes(mount.sizeBytes) : 'not included'}
        {(mount.canInclude || mount.includedByOwner) && <IncludeFolderButton mount={mount} />}
      </span>
    </div>
  )
}

/** Backs up a host folder Stowaway leaves out by default, or stops (the owner's choice, kept per folder). */
function IncludeFolderButton({ mount }: { mount: MountInfo }) {
  const include = useIncludeFolder()
  const including = !mount.included
  return (
    <button
      type="button"
      disabled={include.isPending}
      onClick={() => include.mutate({ path: mount.displaySource, include: including })}
      title={
        include.error
          ? include.error.message
          : including
            ? `Back up ${mount.displaySource} with this app, in every plan that covers it.`
            : `Stop backing up ${mount.displaySource}. Earlier snapshots keep it.`
      }
      className={cn(
        'rounded-md border border-line px-1.5 py-0.5 text-[11px] font-medium opacity-100 transition hover:border-accent hover:text-fg disabled:opacity-50',
        include.error && 'border-danger text-danger',
      )}
    >
      {including ? 'Include' : 'Exclude'}
    </button>
  )
}

/** Windows containers keep files they write inside the container; a recognized product's note replaces the generic one. */
export function WindowsContainerNote({ sqlServer, productNote = null }: { sqlServer: boolean; productNote?: string | null }) {
  return (
    <p className="mt-2 flex items-start gap-1.5 text-[11px] leading-snug text-muted">
      <Info className="mt-px size-3 shrink-0" />
      <span>
        {productNote
          ? productNote
          : sqlServer
            ? 'Windows containers: volumes and SQL Server databases are backed up; other files written inside the container itself aren’t.'
            : 'Windows containers: volumes are backed up, but files written inside the container itself aren’t. Keep data in a volume.'}
      </span>
    </p>
  )
}

/**
 * Says plainly which mapped-in data isn't backed up: Stowaway backs up an app (its volumes, database dumps and
 * its compose project folder), not host folders, drives or shares that happen to be mapped into it.
 */
export function OutsideDataNote({ mounts }: { mounts: MountInfo[] }) {
  const outside = mounts.filter((m) => !m.included && (m.kind === 'bind' || m.kind === 'volume'))
  if (outside.length === 0) return null
  const what = outside.length === 1 ? 'One mapped folder or share isn’t' : `${outside.length} mapped folders or shares aren’t`
  return (
    <p className="mt-2 flex items-start gap-1.5 text-[11px] leading-snug text-muted" title={[...new Set(outside.map((m) => m.exclusionReason))].join('\n')}>
      <Info className="mt-px size-3 shrink-0" />
      <span>
        {what} included: {outside.length === 1 ? 'it lives' : 'they live'} outside the app, on the host or another computer. Back{' '}
        {outside.length === 1 ? 'it' : 'them'} up with your file backup
        {outside.some((m) => m.canInclude) ? ', or choose Include to back up a folder with this app.' : '.'}
      </span>
    </p>
  )
}
