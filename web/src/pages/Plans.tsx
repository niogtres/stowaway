import { motion } from 'motion/react'
import { CalendarClock, HardDrive, Pencil, Play, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import emptyPlans from '@/assets/illustrations/empty-plans.webp'
import { PlanWizard } from '@/components/PlanWizard'
import { Button, Dialog } from '@/components/forms'
import { Badge, Card, Skeleton, StatusDot } from '@/components/ui'
import { useDeletePlan, useDestinations, useInventory, usePlans, useRunPlan, useSavePlan, type Plan, type RunStatus } from '@/lib/api'
import { describeRetention, describeSchedule } from '@/lib/schedule'
import { cn, relativeTime } from '@/lib/utils'

export function Plans() {
  const plans = usePlans()
  const destinations = useDestinations()
  const [editing, setEditing] = useState<Plan | undefined>()
  const [wizardOpen, setWizardOpen] = useState(false)
  const noDestination = destinations.data?.length === 0

  const openWizard = (plan?: Plan) => {
    setEditing(plan)
    setWizardOpen(true)
  }

  if (plans.isPending) return <Skeleton className="mx-auto h-48 max-w-5xl rounded-2xl" />

  return (
    <div className="mx-auto max-w-5xl">
      {(plans.data ?? []).length === 0 ? (
        <div className="mx-auto max-w-xl pt-6">
          <Card className="flex flex-col items-center gap-4 px-10 pb-12 pt-6 text-center">
            <img src={emptyPlans} alt="" width={320} height={240} className="h-60 w-80 select-none object-contain" draggable={false} />
            <h2 className="text-lg font-semibold tracking-tight">Set it and forget it</h2>
            <p className="text-sm text-muted">
              A plan backs up your apps on a schedule, keeps a sensible history and tells you when something goes wrong.
            </p>
            <Button variant="primary" onClick={() => openWizard()} disabled={noDestination}>
              <Plus className="size-4" /> Create your first plan
            </Button>
            {noDestination && <p className="text-xs text-warn">Add a destination first.</p>}
          </Card>
        </div>
      ) : (
        <>
          <div className="mb-5 flex justify-end">
            <Button variant="primary" onClick={() => openWizard()} disabled={noDestination}>
              <Plus className="size-4" /> New plan
            </Button>
          </div>
          <div className="grid gap-4">
            {plans.data!.map((plan, i) => (
              <motion.div key={plan.id} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0, transition: { delay: i * 0.04 } }}>
                <PlanCard plan={plan} onEdit={() => openWizard(plan)} />
              </motion.div>
            ))}
          </div>
        </>
      )}
      {/* Mounted only while open, keyed by plan, so every opening starts from fresh state. */}
      {wizardOpen && <PlanWizard key={editing?.id ?? 'new'} open onClose={() => setWizardOpen(false)} plan={editing} />}
    </div>
  )
}

const runTone: Record<RunStatus, 'ok' | 'danger' | 'muted' | 'accent' | 'warn'> = {
  succeeded: 'ok',
  failed: 'danger',
  cancelled: 'muted',
  running: 'accent',
  queued: 'warn',
}

function PlanCard({ plan, onEdit }: { plan: Plan; onEdit: () => void }) {
  const destinations = useDestinations()
  const inventory = useInventory()
  const run = useRunPlan()
  const save = useSavePlan()
  const remove = useDeletePlan()
  const [confirmDelete, setConfirmDelete] = useState(false)
  const destination = destinations.data?.find((d) => d.id === plan.destinationId)
  const names = new Map((inventory.data?.snapshot?.apps ?? []).map((a) => [a.id, a.name]))
  const items = plan.coveredItemIds.map((id) => names.get(id) ?? id.replace(/^volume:/, 'volume '))

  const toggle = () => {
    const { id: _id, createdAt: _c, nextRunAt: _n, intervalHours: _i, coveredItemIds: _ci, recentRuns: _r, ...input } = plan
    save.mutate({ id: plan.id, input: { ...input, enabled: !plan.enabled } })
  }

  return (
    <Card className={cn(!plan.enabled && 'opacity-70')}>
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="flex min-w-0 items-start gap-3">
          <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-accent-soft">
            <CalendarClock className="size-5 text-accent" />
          </div>
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <h3 className="font-semibold tracking-tight">{plan.name}</h3>
              {!plan.enabled && <Badge>Paused</Badge>}
            </div>
            <div className="mt-0.5 text-sm text-muted">
              {describeSchedule(plan.cron)} · {plan.enabled && plan.nextRunAt ? `next ${relativeTime(plan.nextRunAt)}` : 'not scheduled'}
            </div>
          </div>
        </div>
        <div className="flex items-center gap-1">
          <label className="mr-2 flex cursor-pointer items-center gap-2 text-xs text-muted" title={plan.enabled ? 'Pause plan' : 'Resume plan'}>
            <button
              type="button"
              role="switch"
              aria-checked={plan.enabled}
              onClick={toggle}
              className={cn('relative h-5 w-9 rounded-full transition', plan.enabled ? 'bg-accent' : 'bg-line')}
            >
              <span className={cn('absolute top-0.5 size-4 rounded-full bg-bg shadow transition-all', plan.enabled ? 'left-[1.125rem]' : 'left-0.5')} />
            </button>
            {plan.enabled ? 'On' : 'Off'}
          </label>
          <Button variant="secondary" className="text-xs" onClick={() => run.mutate(plan.id)} disabled={run.isPending}>
            <Play className="size-3.5" /> Run now
          </Button>
          <Button variant="ghost" onClick={onEdit} aria-label="Edit plan">
            <Pencil className="size-4" />
          </Button>
          <Button variant="ghost" onClick={() => setConfirmDelete(true)} aria-label="Delete plan">
            <Trash2 className="size-4" />
          </Button>
        </div>
      </div>

      <div className="mt-4 grid gap-3 border-t border-line pt-4 text-sm md:grid-cols-[1fr_auto]">
        <div className="min-w-0">
          <div className="text-xs text-muted">
            {plan.selection === 'allApps' ? 'All apps' : 'Selected apps'} · {items.length} item{items.length === 1 ? '' : 's'}
            {plan.excludedAppIds.length > 0 && ` · ${plan.excludedAppIds.length} excluded`}
          </div>
          <div className="mt-1.5 flex flex-wrap gap-1.5">
            {items.slice(0, 10).map((n) => (
              <Badge key={n}>{n}</Badge>
            ))}
            {items.length > 10 && <Badge>+{items.length - 10} more</Badge>}
          </div>
          <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted">
            <span className="flex items-center gap-1.5">
              <HardDrive className="size-3.5" /> {destination?.name ?? 'missing destination'}
            </span>
            <span>{describeRetention(plan.retention)}</span>
            <span>{plan.strategy === 'auto' ? 'Automatic consistency' : plan.strategy === 'live' ? 'Live copies' : 'Stop & start'}</span>
          </div>
        </div>
        <div className="flex items-end gap-1" aria-label="Recent runs">
          {plan.recentRuns.length === 0 ? (
            <span className="text-xs text-muted">No runs yet</span>
          ) : (
            [...plan.recentRuns].reverse().map((r) => (
              <span key={r.id} title={`${r.appName}: ${r.status}${r.error ? ` (${r.error})` : ''} · ${relativeTime(r.finishedAt ?? r.queuedAt)}`}>
                <StatusDot tone={runTone[r.status]} />
              </span>
            ))
          )}
        </div>
      </div>
      {run.data && <p className="mt-2 text-xs text-muted">{run.data.queued} backup(s) queued.</p>}
      {run.error && <p className="mt-2 text-xs text-danger">{run.error.message}</p>}

      <Dialog
        open={confirmDelete}
        onClose={() => setConfirmDelete(false)}
        title={`Delete ${plan.name}?`}
        description="Scheduled backups stop. Existing snapshots stay in the destination."
        footer={
          <>
            <Button onClick={() => setConfirmDelete(false)}>Keep it</Button>
            <Button variant="danger" onClick={() => remove.mutate(plan.id, { onSuccess: () => setConfirmDelete(false) })}>
              Delete plan
            </Button>
          </>
        }
      >
        {null}
      </Dialog>
    </Card>
  )
}
