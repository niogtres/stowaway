import { motion } from 'motion/react'
import { Link } from '@tanstack/react-router'
import { ArrowRight, Boxes, CheckCircle2, Circle, Cpu, Database, HeartPulse, Layers, PackageOpen, ShieldAlert, ShieldCheck, TriangleAlert } from 'lucide-react'
import engineOffline from '@/assets/illustrations/engine-offline.webp'
import { ProtectionRing } from '@/components/ProtectionRing'
import { Badge, Card, CardHeader, Skeleton, Stat, StatusDot } from '@/components/ui'
import { useDestinations, useInventory, useSystemInfo, type InventorySnapshot, type SystemInfo } from '@/lib/api'
import { cn, formatBytes, formatNumber } from '@/lib/utils'

const rise = {
  hidden: { opacity: 0, y: 12 },
  show: (i: number) => ({ opacity: 1, y: 0, transition: { delay: i * 0.06, duration: 0.4, ease: [0.22, 1, 0.36, 1] as const } }),
}

function greeting() {
  const hour = new Date().getHours()
  return hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening'
}

export function Dashboard() {
  const { data, isPending } = useSystemInfo()
  const inventory = useInventory()
  const destinations = useDestinations()
  const snapshot = inventory.data?.snapshot ?? null
  const score = snapshot?.totals.protectionScore ?? null

  return (
    <div className="mx-auto max-w-6xl">
      <p className="mb-6 text-muted">
        {greeting()}. Here's how protected <span className="text-fg">{data?.machineName ?? 'this host'}</span> is.
      </p>

      <div className="grid gap-5 lg:grid-cols-3">
        <motion.div variants={rise} initial="hidden" animate="show" custom={0} className="lg:row-span-2">
          <Card className="flex h-full flex-col items-center justify-center gap-5 py-8 text-center">
            <ProtectionRing score={score} />
            <div>
              <div className="font-medium">Protection score</div>
              <p className="mx-auto mt-1 max-w-64 text-sm text-muted">{scoreCaption(snapshot)}</p>
            </div>
          </Card>
        </motion.div>

        <motion.div variants={rise} initial="hidden" animate="show" custom={1} className="lg:col-span-2">
          <EngineCard data={data} loading={isPending} />
        </motion.div>

        <motion.div variants={rise} initial="hidden" animate="show" custom={2}>
          <DataCard snapshot={snapshot} loading={inventory.isPending} />
        </motion.div>

        <motion.div variants={rise} initial="hidden" animate="show" custom={3}>
          <GettingStarted engineConnected={data?.engine.reachable ?? false} discovered={snapshot !== null} hasDestination={(destinations.data?.length ?? 0) > 0} />
        </motion.div>

        {snapshot && (
          <motion.div variants={rise} initial="hidden" animate="show" custom={4} className="lg:col-span-3">
            <NeedsAttention snapshot={snapshot} />
          </motion.div>
        )}
      </div>
    </div>
  )
}

function scoreCaption(snapshot: InventorySnapshot | null) {
  if (!snapshot) return 'Shows the share of your Docker data covered by a healthy backup plan.'
  const t = snapshot.totals
  if (t.protectionScore === null) return 'Measuring your data…'
  if (t.protectedBytes === 0) return `None of the ${formatBytes(t.dataBytes)} found is backed up yet. Backup plans arrive in an upcoming release.`
  return `${formatBytes(t.protectedBytes)} of ${formatBytes(t.dataBytes)} is covered by a healthy plan.`
}

function EngineCard({ data, loading }: { data?: SystemInfo; loading: boolean }) {
  const engine = data?.engine
  return (
    <Card>
      <CardHeader
        title="Docker engine"
        icon={<Cpu className="size-4" />}
        action={
          engine &&
          (engine.reachable ? (
            <Badge tone="ok">
              <StatusDot tone="ok" /> Connected
            </Badge>
          ) : (
            <Badge tone="warn">
              <StatusDot tone="warn" /> Unavailable
            </Badge>
          ))
        }
      />
      {loading || !engine ? (
        <div className="grid grid-cols-4 gap-4">
          {Array.from({ length: 4 }, (_, i) => (
            <Skeleton key={i} className="h-14" />
          ))}
        </div>
      ) : engine.reachable ? (
        <>
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
            <Stat label="Running" value={formatNumber(engine.containersRunning)} hint="containers" />
            <Stat label="Stopped" value={formatNumber(engine.containersStopped)} hint="containers" />
            <Stat label="Images" value={formatNumber(engine.images)} />
            <Stat label="Engine" value={engine.serverVersion} hint={`API ${engine.apiVersion} · ${engine.osType}`} />
          </div>
          <div className="mt-4 truncate border-t border-line pt-3 font-mono text-xs text-muted">{engine.endpoint}</div>
        </>
      ) : (
        <div className="flex items-center gap-5">
          <img src={engineOffline} alt="" width={160} height={120} className="h-30 w-40 shrink-0 select-none object-contain" draggable={false} />
          <div className="min-w-0 text-sm">
            <p>The service can't reach Docker right now. If you use Docker Desktop, it only runs while you're signed in.</p>
            <p className="mt-2 break-all font-mono text-xs text-muted">{engine.error}</p>
          </div>
        </div>
      )}
    </Card>
  )
}

function DataCard({ snapshot, loading }: { snapshot: InventorySnapshot | null; loading: boolean }) {
  return (
    <Card className="h-full">
      <CardHeader
        title="Data discovered"
        icon={<Boxes className="size-4" />}
        action={
          <Link to="/inventory" className="flex items-center gap-1 text-xs text-muted transition hover:text-fg">
            Inventory <ArrowRight className="size-3" />
          </Link>
        }
      />
      {loading || !snapshot ? (
        <Skeleton className="h-16" />
      ) : (
        <div className="grid grid-cols-2 gap-4">
          <Stat label="Total" value={formatBytes(snapshot.totals.dataBytes)} hint={`${snapshot.totals.apps} apps`} />
          <Stat label="Volumes" value={formatNumber(snapshot.totals.volumes)} hint={`${snapshot.totals.bindMounts} bind mounts`} />
        </div>
      )}
    </Card>
  )
}

function GettingStarted({ engineConnected, discovered, hasDestination }: { engineConnected: boolean; discovered: boolean; hasDestination: boolean }) {
  const steps = [
    { label: 'Connect to Docker', done: engineConnected },
    { label: 'Discover containers and volumes', done: discovered },
    { label: 'Add a backup destination', done: hasDestination },
    { label: 'Create your first plan', done: false },
  ]
  return (
    <Card className="h-full">
      <CardHeader title="Getting started" icon={<ShieldCheck className="size-4" />} />
      <ol className="space-y-2.5 text-sm">
        {steps.map((step) => (
          <li key={step.label} className="flex items-center gap-2.5">
            {step.done ? <CheckCircle2 className="size-4 text-ok" /> : <Circle className="size-4 text-muted" />}
            <span className={step.done ? 'text-muted line-through decoration-line' : ''}>{step.label}</span>
          </li>
        ))}
      </ol>
    </Card>
  )
}

interface AttentionItem {
  tone: 'warn' | 'danger' | 'accent'
  icon: typeof TriangleAlert
  title: string
  detail: string
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`

function attentionItems(snapshot: InventorySnapshot): AttentionItem[] {
  const t = snapshot.totals
  const containers = snapshot.apps.flatMap((a) => a.containers)
  const items: AttentionItem[] = []

  const unhealthy = containers.filter((c) => c.health === 'unhealthy')
  if (unhealthy.length > 0)
    items.push({
      tone: 'danger',
      icon: HeartPulse,
      title: plural(unhealthy.length, 'unhealthy container'),
      detail: unhealthy.map((c) => c.name).join(', '),
    })

  const unprotectedApps = snapshot.apps.filter((a) => a.protection === 'unprotected')
  if (unprotectedApps.length > 0)
    items.push({
      tone: 'warn',
      icon: ShieldAlert,
      title: `${plural(unprotectedApps.length, 'app')} not backed up`,
      detail: `${formatBytes(unprotectedApps.reduce((sum, a) => sum + a.dataBytes, 0))} of data with no plan covering it.`,
    })

  const orphans = snapshot.volumes.filter((v) => v.orphaned)
  if (orphans.length > 0)
    items.push({
      tone: 'warn',
      icon: Layers,
      title: plural(orphans.length, 'unowned volume'),
      detail: `${formatBytes(orphans.reduce((sum, v) => sum + (v.sizeBytes ?? 0), 0))} that no container uses. Review before anything cleans them up.`,
    })

  if (t.localOnlyImages > 0)
    items.push({
      tone: 'accent',
      icon: PackageOpen,
      title: plural(t.localOnlyImages, 'locally built image'),
      detail: "These can't be pulled from a registry after a disaster, so include the images in backups.",
    })

  if (t.databases > 0)
    items.push({
      tone: 'accent',
      icon: Database,
      title: `${plural(t.databases, 'database')} detected`,
      detail: 'Copying live database files can give a corrupt backup. These will get consistent, dump-based backups.',
    })

  return items
}

const toneText = { warn: 'text-warn', danger: 'text-danger', accent: 'text-accent' } as const

function NeedsAttention({ snapshot }: { snapshot: InventorySnapshot }) {
  const items = attentionItems(snapshot)
  return (
    <Card>
      <CardHeader title="Needs attention" icon={<TriangleAlert className="size-4" />} />
      {items.length === 0 ? (
        <p className="text-sm text-muted">Nothing needs your attention right now.</p>
      ) : (
        <ul className="grid gap-3 md:grid-cols-2">
          {items.map(({ tone, icon: Icon, title, detail }) => (
            <li key={title}>
              <Link to="/inventory" className="flex gap-3 rounded-xl p-2 transition hover:bg-accent-soft">
                <span className={cn('mt-0.5 grid size-8 shrink-0 place-items-center rounded-lg bg-accent-soft', toneText[tone])}>
                  <Icon className="size-4" />
                </span>
                <span className="min-w-0">
                  <span className="block text-sm font-medium">{title}</span>
                  <span className="block text-xs text-muted">{detail}</span>
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}
