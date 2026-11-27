import { Ban, Database, FolderOpen, HardDrive, Layers, PackageOpen } from 'lucide-react'
import { databaseLabels, type ContainerInfo, type MountInfo, type ProtectionStatus } from '@/lib/api'
import { cn, formatBytes, shortVolumeName } from '@/lib/utils'
import { Badge, StatusDot } from './ui'

const protectionLabels: Record<ProtectionStatus, string> = {
  protected: 'Protected',
  stale: 'Stale',
  failing: 'Failing',
  unprotected: 'Unprotected',
  excluded: 'Excluded',
  stateless: 'Stateless',
}

const protectionTones: Record<ProtectionStatus, 'ok' | 'warn' | 'danger' | 'muted'> = {
  protected: 'ok',
  stale: 'warn',
  failing: 'danger',
  unprotected: 'warn',
  excluded: 'muted',
  stateless: 'muted',
}

const protectionHints: Record<ProtectionStatus, string> = {
  protected: 'Covered by a healthy plan',
  stale: 'Covered, but the last successful backup is overdue',
  failing: 'Covered, but the last backup failed',
  unprotected: 'Holds data that no plan covers yet',
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
        </div>
        <div className="mt-0.5 truncate font-mono text-[11px] text-muted" title={container.image}>
          {container.image} · {container.statusText}
        </div>
      </div>
    </div>
  )
}

const mountIcons = { volume: Layers, bind: FolderOpen, tmpfs: HardDrive, npipe: HardDrive, other: HardDrive }

export function MountRow({ mount, sharedBy = 1 }: { mount: MountInfo; sharedBy?: number }) {
  const Icon = mount.included ? mountIcons[mount.kind] : Ban
  const source = mount.kind === 'volume' ? shortVolumeName(mount.displaySource, mount.anonymous) : mount.displaySource
  return (
    <div
      className={cn('grid grid-cols-[1rem_minmax(0,1fr)_auto] items-center gap-x-2.5 py-1 text-xs', !mount.included && 'opacity-55')}
      title={mount.exclusionReason ?? `${mount.kind} · ${mount.displaySource} → ${mount.destination}${mount.readOnly ? ' (read-only)' : ''}`}
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
      <span className="tabular-nums text-muted">{mount.included ? formatBytes(mount.sizeBytes) : 'excluded'}</span>
    </div>
  )
}
