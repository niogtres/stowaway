import { motion } from 'motion/react'
import { CheckCircle2, Circle, Cpu, ServerCog, ShieldCheck } from 'lucide-react'
import engineOffline from '@/assets/illustrations/engine-offline.webp'
import { ProtectionRing } from '@/components/ProtectionRing'
import { Badge, Card, CardHeader, Skeleton, Stat, StatusDot } from '@/components/ui'
import { displayVersion, useSystemInfo, type SystemInfo } from '@/lib/api'
import { formatDuration, formatNumber } from '@/lib/utils'

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

  return (
    <div className="mx-auto max-w-6xl">
      <p className="mb-6 text-muted">
        {greeting()}. Here's how protected <span className="text-fg">{data?.machineName ?? 'this host'}</span> is.
      </p>

      <div className="grid gap-5 lg:grid-cols-3">
        <motion.div variants={rise} initial="hidden" animate="show" custom={0} className="lg:row-span-2">
          <Card className="flex h-full flex-col items-center justify-center gap-5 py-8 text-center">
            <ProtectionRing score={null} />
            <div>
              <div className="font-medium">Protection score</div>
              <p className="mx-auto mt-1 max-w-64 text-sm text-muted">
                Shows the share of your Docker data covered by a healthy backup plan. Discovery arrives in the next release.
              </p>
            </div>
          </Card>
        </motion.div>

        <motion.div variants={rise} initial="hidden" animate="show" custom={1} className="lg:col-span-2">
          <EngineCard data={data} loading={isPending} />
        </motion.div>

        <motion.div variants={rise} initial="hidden" animate="show" custom={2}>
          <ServiceCard data={data} loading={isPending} />
        </motion.div>

        <motion.div variants={rise} initial="hidden" animate="show" custom={3}>
          <GettingStarted engineConnected={data?.engine.reachable ?? false} />
        </motion.div>
      </div>
    </div>
  )
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

function ServiceCard({ data, loading }: { data?: SystemInfo; loading: boolean }) {
  return (
    <Card className="h-full">
      <CardHeader title="Service" icon={<ServerCog className="size-4" />} />
      {loading || !data ? (
        <Skeleton className="h-16" />
      ) : (
        <div className="grid grid-cols-2 gap-4">
          <Stat label="Version" value={displayVersion(data.version)} hint={data.runningAsService ? 'Windows Service' : 'Console mode'} />
          <Stat label="Uptime" value={formatDuration(data.startedAt)} hint={data.machineName} />
        </div>
      )}
    </Card>
  )
}

function GettingStarted({ engineConnected }: { engineConnected: boolean }) {
  const steps = [
    { label: 'Connect to Docker', done: engineConnected },
    { label: 'Discover containers and volumes', done: false },
    { label: 'Add a backup destination', done: false },
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
