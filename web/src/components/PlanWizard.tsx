import { Boxes, Check, ChevronLeft, ChevronRight, Database, FolderClosed, Network, PauseCircle, Search, Sparkles, Zap } from 'lucide-react'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Button, Dialog, Field, TextInput } from '@/components/forms'
import { Badge } from '@/components/ui'
import {
  previewSchedule,
  useDestinations,
  useInventory,
  useSavePlan,
  type AppInfo,
  type Plan,
  type PlanInput,
  type PlanStrategy,
  type RetentionPolicy,
  type SchedulePreview,
} from '@/lib/api'
import {
  defaultSchedule,
  describeRetention,
  describeSchedule,
  fromCron,
  presetFor,
  retentionPresets,
  toCron,
  weekdays,
  type RetentionPreset,
  type ScheduleChoice,
  type ScheduleKind,
} from '@/lib/schedule'
import { cn, formatBytes } from '@/lib/utils'

const steps = ['What', 'How', 'Where', 'When & keep'] as const

const hasData = (app: AppInfo) => app.containers.some((c) => c.mounts.some((m) => m.included))
const runsDatabase = (app: AppInfo) => app.containers.some((c) => c.database && c.state === 'running')
const runsUndumpableDatabase = (app: AppInfo) => app.containers.some((c) => c.database && c.state === 'running' && !c.dumpSupported)

function initialInput(plan: Plan | undefined, destinationId: string | undefined): PlanInput {
  if (plan) {
    const { name, enabled, selection, appIds, excludedAppIds, includeOrphanedVolumes, strategy, destinationId: d, cron, retention, verifyRestores } = plan
    return { name, enabled, selection, appIds, excludedAppIds, includeOrphanedVolumes, strategy, destinationId: d, cron, retention, verifyRestores }
  }
  return {
    name: 'Nightly backup',
    enabled: true,
    selection: 'allApps',
    appIds: [],
    excludedAppIds: [],
    includeOrphanedVolumes: false,
    strategy: 'auto',
    destinationId: destinationId ?? '',
    cron: toCron(defaultSchedule),
    retention: retentionPresets.standard.policy,
    verifyRestores: true,
  }
}

export function PlanWizard({ open, onClose, plan }: { open: boolean; onClose: () => void; plan?: Plan }) {
  const inventory = useInventory()
  const destinations = useDestinations()
  const save = useSavePlan()
  const [step, setStep] = useState(0)
  const [input, setInput] = useState<PlanInput>(() => initialInput(plan, destinations.data?.[0]?.id))
  const [schedule, setSchedule] = useState<ScheduleChoice>(() => (plan ? fromCron(plan.cron) : defaultSchedule))
  const [preset, setPreset] = useState<RetentionPreset>(() => presetFor(input.retention))
  const [preview, setPreview] = useState<SchedulePreview | null>(null)

  const cron = toCron(schedule)
  useEffect(() => {
    if (!open) return
    const handle = setTimeout(() => {
      previewSchedule(cron).then(setPreview).catch(() => setPreview(null))
    }, 250)
    return () => clearTimeout(handle)
  }, [cron, open])

  const apps = useMemo(() => inventory.data?.snapshot?.apps ?? [], [inventory.data])
  const orphans = useMemo(() => (inventory.data?.snapshot?.volumes ?? []).filter((v) => v.orphaned && v.sizeBytes !== 0), [inventory.data])
  const dataApps = apps.filter(hasData)
  const covered = dataApps.filter((a) => (input.selection === 'allApps' ? !input.excludedAppIds.includes(a.id) : input.appIds.includes(a.id)))
  const coveredBytes = covered.reduce((s, a) => s + a.dataBytes, 0) + (input.includeOrphanedVolumes ? orphans.reduce((s, v) => s + (v.sizeBytes ?? 0), 0) : 0)
  const stopping = covered.filter((a) => input.strategy === 'stopStart' || (input.strategy === 'auto' && runsUndumpableDatabase(a)))
  const dumped = input.strategy === 'stopStart' ? [] : covered.filter(runsDatabase).filter((a) => !stopping.includes(a))
  // Defaults to the first destination once destinations have loaded.
  const destinationId = input.destinationId || destinations.data?.[0]?.id || ''
  const destination = destinations.data?.find((d) => d.id === destinationId)

  const update = (patch: Partial<PlanInput>) => setInput((i) => ({ ...i, ...patch }))
  const toggle = (list: string[], id: string) => (list.includes(id) ? list.filter((x) => x !== id) : [...list, id])

  const stepValid = [
    covered.length > 0 || input.includeOrphanedVolumes,
    true,
    !!destinationId,
    input.name.trim().length > 0 && (preview?.valid ?? true),
  ]

  const submit = () =>
    save.mutate({ id: plan?.id, input: { ...input, destinationId, cron } }, { onSuccess: onClose })

  return (
    <Dialog open={open} onClose={onClose} title={plan ? `Edit ${plan.name}` : 'New backup plan'} className="w-[min(46rem,calc(100vw-2rem))]">
      <ol className="mb-6 flex items-center gap-2" aria-label="Steps">
        {steps.map((label, i) => (
          <li key={label} className="flex flex-1 items-center gap-2">
            <button
              type="button"
              onClick={() => i < step && setStep(i)}
              className={cn(
                'flex items-center gap-2 text-xs font-medium transition',
                i === step ? 'text-fg' : i < step ? 'text-accent hover:underline' : 'text-muted',
              )}
              aria-current={i === step ? 'step' : undefined}
            >
              <span className={cn('grid size-6 place-items-center rounded-full border text-[11px]', i === step ? 'border-accent bg-accent-soft' : i < step ? 'border-accent bg-accent text-bg' : 'border-line')}>
                {i < step ? <Check className="size-3.5" /> : i + 1}
              </span>
              {label}
            </button>
            {i < steps.length - 1 && <span className="h-px flex-1 bg-line" />}
          </li>
        ))}
      </ol>

      <div className="min-h-80">
        {step === 0 && (
          <div className="space-y-4">
            <div className="grid grid-cols-2 gap-2">
              <Choice
                active={input.selection === 'allApps'}
                onClick={() => update({ selection: 'allApps' })}
                icon={Sparkles}
                title="All apps"
                badge="Recommended"
                text="Everything that holds data, including apps you add later."
              />
              <Choice active={input.selection === 'selected'} onClick={() => update({ selection: 'selected' })} icon={Boxes} title="Selected apps" text="Only the apps you tick." />
            </div>
            <AppPicker
              apps={dataApps}
              checked={(id) => (input.selection === 'allApps' ? !input.excludedAppIds.includes(id) : input.appIds.includes(id))}
              onToggle={(id) =>
                input.selection === 'allApps' ? update({ excludedAppIds: toggle(input.excludedAppIds, id) }) : update({ appIds: toggle(input.appIds, id) })
              }
              hint={input.selection === 'allApps' ? 'Untick apps to leave them out.' : 'Tick the apps to include.'}
            />
            {orphans.length > 0 && (
              <label className="flex items-start gap-2 text-sm">
                <input type="checkbox" className="mt-0.5 accent-[var(--accent)]" checked={input.includeOrphanedVolumes} onChange={(e) => update({ includeOrphanedVolumes: e.target.checked })} />
                <span>
                  Also back up the {orphans.length} unowned volume{orphans.length === 1 ? '' : 's'} ({formatBytes(orphans.reduce((s, v) => s + (v.sizeBytes ?? 0), 0))})
                  <span className="block text-xs text-muted">Volumes no container uses. Each is saved as its own item.</span>
                </span>
              </label>
            )}
          </div>
        )}

        {step === 1 && (
          <div className="space-y-4">
            <div className="grid grid-cols-3 gap-2">
              {(
                [
                  { id: 'auto', icon: Sparkles, title: 'Automatic', badge: 'Recommended', text: 'Dumps databases while they run; only stops an app if its database can’t be dumped.' },
                  { id: 'live', icon: Zap, title: 'Live', text: 'Never stops anything. Supported databases are still dumped.' },
                  { id: 'stopStart', icon: PauseCircle, title: 'Stop & start', text: 'Stops every app briefly for a fully consistent copy.' },
                ] as { id: PlanStrategy; icon: typeof Zap; title: string; badge?: string; text: string }[]
              ).map((o) => (
                <Choice key={o.id} active={input.strategy === o.id} onClick={() => update({ strategy: o.id })} icon={o.icon} title={o.title} badge={o.badge} text={o.text} />
              ))}
            </div>
            {dumped.length > 0 && (
              <div className="rounded-xl border border-line p-3 text-sm">
                <span className="font-medium">Databases dumped with their own tools, no downtime:</span>
                <div className="mt-2 flex flex-wrap gap-1.5">
                  {dumped.map((a) => (
                    <Badge key={a.id} tone="accent">
                      <Database className="size-3" />
                      {a.name}
                    </Badge>
                  ))}
                </div>
              </div>
            )}
            <div className="rounded-xl border border-line p-3 text-sm">
              {stopping.length === 0 ? (
                <span className="text-muted">No app will be stopped.</span>
              ) : (
                <>
                  <span className="font-medium">
                    {stopping.length} app{stopping.length === 1 ? '' : 's'} will be stopped briefly during each backup:
                  </span>
                  <div className="mt-2 flex flex-wrap gap-1.5">
                    {stopping.map((a) => (
                      <Badge key={a.id} tone={runsDatabase(a) ? 'accent' : 'muted'}>
                        {runsDatabase(a) && <Database className="size-3" />}
                        {a.name}
                      </Badge>
                    ))}
                  </div>
                </>
              )}
            </div>
          </div>
        )}

        {step === 2 && (
          <div className="grid gap-2">
            {(destinations.data ?? []).length === 0 && <p className="text-sm text-muted">Add a destination first (Destinations page).</p>}
            {(destinations.data ?? []).map((d) => (
              <button
                key={d.id}
                type="button"
                role="radio"
                aria-checked={destinationId === d.id}
                onClick={() => update({ destinationId: d.id })}
                className={cn('flex items-center gap-3 rounded-xl border p-3 text-left transition', destinationId === d.id ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted')}
              >
                {d.kind === 'smb' ? <Network className="size-4 text-accent" /> : <FolderClosed className="size-4 text-accent" />}
                <span className="min-w-0 flex-1">
                  <span className="block text-sm font-medium">{d.name}</span>
                  <span className="block truncate font-mono text-xs text-muted">{d.path}</span>
                </span>
                {d.status && <Badge tone={d.status.reachable ? 'ok' : 'danger'}>{d.status.reachable ? 'Healthy' : 'Unreachable'}</Badge>}
              </button>
            ))}
          </div>
        )}

        {step === 3 && (
          <div className="space-y-5">
            <div className="grid grid-cols-[1fr_1fr] gap-5">
              <div className="space-y-3">
                <span className="block text-xs font-medium text-muted">Schedule</span>
                <select
                  value={schedule.kind}
                  onChange={(e) => setSchedule((s) => ({ ...s, kind: e.target.value as ScheduleKind, cron: s.kind === 'custom' ? s.cron : toCron(s) }))}
                  className="w-full rounded-xl border border-line bg-surface-strong px-3 py-2 text-sm"
                  aria-label="Schedule"
                >
                  <option value="hourly">Every hour</option>
                  <option value="every6h">Every 6 hours</option>
                  <option value="daily">Daily</option>
                  <option value="weekly">Weekly</option>
                  <option value="custom">Custom (cron)</option>
                </select>
                {(schedule.kind === 'daily' || schedule.kind === 'weekly') && (
                  <div className="flex gap-2">
                    {schedule.kind === 'weekly' && (
                      <select
                        value={schedule.weekday}
                        onChange={(e) => setSchedule((s) => ({ ...s, weekday: +e.target.value }))}
                        className="flex-1 rounded-xl border border-line bg-surface-strong px-3 py-2 text-sm"
                        aria-label="Day"
                      >
                        {weekdays.map((d, i) => (
                          <option key={d} value={i}>
                            {d}
                          </option>
                        ))}
                      </select>
                    )}
                    <TextInput type="time" value={schedule.time} onChange={(e) => setSchedule((s) => ({ ...s, time: e.target.value }))} className="flex-1" aria-label="Time" />
                  </div>
                )}
                {schedule.kind === 'custom' && (
                  <Field label="Cron (minute hour day month weekday)" error={preview && !preview.valid ? preview.error : null}>
                    <TextInput value={schedule.cron} onChange={(e) => setSchedule((s) => ({ ...s, cron: e.target.value }))} className="font-mono" />
                  </Field>
                )}
                {preview?.valid && (
                  <div className="text-xs text-muted">
                    Next runs:
                    <ul className="mt-1 space-y-0.5 tabular-nums text-fg">
                      {preview.nextRuns.slice(0, 3).map((r) => (
                        <li key={r}>{new Date(r).toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}</li>
                      ))}
                    </ul>
                  </div>
                )}
              </div>

              <div className="space-y-3">
                <span className="block text-xs font-medium text-muted">Keep</span>
                <div className="grid gap-1.5">
                  {(Object.entries(retentionPresets) as [Exclude<RetentionPreset, 'custom'>, (typeof retentionPresets)['standard']][]).map(([key, p]) => (
                    <label key={key} className={cn('flex cursor-pointer items-center gap-2 rounded-xl border px-3 py-2 text-sm', preset === key ? 'border-accent bg-accent-soft' : 'border-line')}>
                      <input
                        type="radio"
                        name="retention"
                        className="accent-[var(--accent)]"
                        checked={preset === key}
                        onChange={() => {
                          setPreset(key)
                          update({ retention: p.policy })
                        }}
                      />
                      <span className="whitespace-nowrap font-medium">{p.label}</span>
                      <span className="text-xs text-muted">{p.text}</span>
                    </label>
                  ))}
                  <label className={cn('flex cursor-pointer items-center gap-2 rounded-xl border px-3 py-2 text-sm', preset === 'custom' ? 'border-accent bg-accent-soft' : 'border-line')}>
                    <input type="radio" name="retention" className="accent-[var(--accent)]" checked={preset === 'custom'} onChange={() => setPreset('custom')} />
                    <span className="font-medium">Custom</span>
                  </label>
                </div>
                {preset === 'custom' && <RetentionEditor value={input.retention} onChange={(retention) => update({ retention })} />}
              </div>
            </div>
            <Field label="Plan name">
              <TextInput value={input.name} onChange={(e) => update({ name: e.target.value })} />
            </Field>
            <label className="flex items-start gap-2 text-sm">
              <input type="checkbox" className="mt-0.5 accent-[var(--accent)]" checked={input.verifyRestores} onChange={(e) => update({ verifyRestores: e.target.checked })} />
              <span>
                Prove the backups restore, weekly
                <span className="block text-xs text-muted">Restores the latest backup of each app into scratch space and loads its database dumps into throwaway containers. Your apps aren't touched.</span>
              </span>
            </label>
          </div>
        )}
      </div>

      <div className="mt-6 rounded-xl bg-accent-soft px-4 py-3 text-sm" aria-live="polite">
        Backs up <strong>{covered.length} app{covered.length === 1 ? '' : 's'}</strong>
        {input.includeOrphanedVolumes && orphans.length > 0 && ` + ${orphans.length} unowned volumes`} · ~{formatBytes(coveredBytes)}
        {stopping.length > 0 && ` · stops ${stopping.length} briefly`}
        {destination && ` · to ${destination.name}`} · {describeSchedule(cron)} · {describeRetention(input.retention).toLowerCase()}
        {input.verifyRestores && ' · verified weekly'}
      </div>

      {save.error && <p className="mt-3 text-sm text-danger">{save.error.message}</p>}

      <footer className="mt-5 flex items-center justify-between">
        <Button variant="ghost" onClick={() => (step === 0 ? onClose() : setStep(step - 1))}>
          <ChevronLeft className="size-4" /> {step === 0 ? 'Cancel' : 'Back'}
        </Button>
        {step < steps.length - 1 ? (
          <Button variant="primary" disabled={!stepValid[step]} onClick={() => setStep(step + 1)}>
            Next <ChevronRight className="size-4" />
          </Button>
        ) : (
          <Button variant="primary" disabled={!stepValid[step] || save.isPending} onClick={submit}>
            <Check className="size-4" /> {plan ? 'Save plan' : 'Create plan'}
          </Button>
        )}
      </footer>
    </Dialog>
  )
}

function Choice({ active, onClick, icon: Icon, title, text, badge }: { active: boolean; onClick: () => void; icon: typeof Zap; title: string; text: string; badge?: string }) {
  return (
    <button type="button" role="radio" aria-checked={active} onClick={onClick} className={cn('rounded-xl border p-3 text-left transition', active ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted')}>
      <span className="flex items-center gap-2 text-sm font-medium">
        <Icon className={cn('size-4', active ? 'text-accent' : 'text-muted')} /> {title}
        {badge && <span className="ml-auto text-[10px] font-medium uppercase tracking-wide text-accent">{badge}</span>}
      </span>
      <span className="mt-1 block text-xs text-muted">{text}</span>
    </button>
  )
}

function AppPicker({ apps, checked, onToggle, hint }: { apps: AppInfo[]; checked: (id: string) => boolean; onToggle: (id: string) => void; hint: ReactNode }) {
  const [term, setTerm] = useState('')
  const shown = apps.filter((a) => a.name.toLowerCase().includes(term.toLowerCase()))
  return (
    <div className="rounded-xl border border-line">
      <div className="flex items-center gap-2 border-b border-line px-3 py-2">
        <Search className="size-4 text-muted" />
        <input value={term} onChange={(e) => setTerm(e.target.value)} placeholder="Filter apps" className="w-full bg-transparent text-sm outline-none placeholder:text-muted" />
        <span className="shrink-0 text-xs text-muted">{hint}</span>
      </div>
      <ul className="max-h-52 overflow-y-auto p-1">
        {shown.map((app) => (
          <li key={app.id}>
            <label className="flex cursor-pointer items-center gap-2.5 rounded-lg px-2 py-1.5 text-sm hover:bg-accent-soft">
              <input type="checkbox" className="accent-[var(--accent)]" checked={checked(app.id)} onChange={() => onToggle(app.id)} />
              <span className="flex-1 truncate">{app.name}</span>
              {runsDatabase(app) && <Database className="size-3.5 text-accent" aria-label="Runs a database" />}
              <span className="tabular-nums text-xs text-muted">{formatBytes(app.dataBytes, { lowerBound: app.sizeIncomplete })}</span>
            </label>
          </li>
        ))}
      </ul>
    </div>
  )
}

function RetentionEditor({ value, onChange }: { value: RetentionPolicy; onChange: (v: RetentionPolicy) => void }) {
  const fields: [keyof RetentionPolicy, string][] = [
    ['keepLast', 'Latest'],
    ['keepHourly', 'Hourly'],
    ['keepDaily', 'Daily'],
    ['keepWeekly', 'Weekly'],
    ['keepMonthly', 'Monthly'],
    ['keepYearly', 'Yearly'],
  ]
  return (
    <div className="grid grid-cols-3 gap-2">
      {fields.map(([key, label]) => (
        <Field key={key} label={label}>
          <TextInput type="number" min={0} max={1000} value={value[key]} onChange={(e) => onChange({ ...value, [key]: Math.max(0, Number.parseInt(e.target.value || '0', 10)) })} />
        </Field>
      ))}
    </div>
  )
}
