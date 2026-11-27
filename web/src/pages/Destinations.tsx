import { motion } from 'motion/react'
import { Copy, Eye, FolderClosed, HardDrive, KeyRound, Network, Plus, RefreshCw, ShieldCheck, Trash2, TriangleAlert, Wrench } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Button, Dialog, Field, TextInput } from '@/components/forms'
import { Badge, Card, Skeleton, Stat, StatusDot } from '@/components/ui'
import {
  revealRecoveryKey,
  useCheckDestination,
  useCreateDestination,
  useDeleteDestination,
  useDestinations,
  useRunMaintenance,
  type Destination,
  type DestinationKind,
} from '@/lib/api'
import { cn, formatBytes, relativeTime } from '@/lib/utils'

export function Destinations() {
  const { data, isPending } = useDestinations()
  const [adding, setAdding] = useState(false)

  return (
    <div className="mx-auto max-w-5xl">
      <div className="mb-6 flex items-center justify-between gap-4">
        <p className="max-w-2xl text-sm text-muted">
          Each destination holds an encrypted, deduplicated backup repository. Only changed data is stored after the first backup.
        </p>
        <Button variant="primary" onClick={() => setAdding(true)}>
          <Plus className="size-4" /> Add destination
        </Button>
      </div>

      {isPending ? (
        <Skeleton className="h-40 rounded-2xl" />
      ) : !data || data.length === 0 ? (
        <Card className="flex flex-col items-center gap-3 px-10 py-14 text-center">
          <div className="grid size-14 place-items-center rounded-2xl bg-accent-soft">
            <HardDrive className="size-7 text-accent" />
          </div>
          <h2 className="text-lg font-semibold tracking-tight">No destinations yet</h2>
          <p className="max-w-md text-sm text-muted">
            Add a folder on another disk or a network share. Avoid the same disk as Docker: a backup there won't survive a disk failure.
          </p>
          <Button variant="primary" className="mt-2" onClick={() => setAdding(true)}>
            <Plus className="size-4" /> Add your first destination
          </Button>
        </Card>
      ) : (
        <div className="grid gap-4">
          {data.map((d, i) => (
            <motion.div key={d.id} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0, transition: { delay: i * 0.05 } }}>
              <DestinationCard destination={d} />
            </motion.div>
          ))}
        </div>
      )}

      <AddDestinationDialog open={adding} onClose={() => setAdding(false)} />
    </div>
  )
}

function DestinationCard({ destination: d }: { destination: Destination }) {
  const check = useCheckDestination()
  const remove = useDeleteDestination()
  const maintenance = useRunMaintenance()
  const [showKey, setShowKey] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const status = d.status
  const Icon = d.kind === 'smb' ? Network : FolderClosed

  return (
    <Card>
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="flex min-w-0 items-start gap-3">
          <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-accent-soft">
            <Icon className="size-5 text-accent" />
          </div>
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <h3 className="font-semibold tracking-tight">{d.name}</h3>
              <Badge>{d.kind === 'smb' ? 'Network share' : 'Local folder'}</Badge>
            </div>
            <div className="mt-0.5 truncate font-mono text-xs text-muted">{d.path}</div>
          </div>
        </div>
        <div className="flex items-center gap-1">
          {status ? (
            <Badge tone={status.reachable ? 'ok' : 'danger'}>
              <StatusDot tone={status.reachable ? 'ok' : 'danger'} />
              {status.reachable ? 'Healthy' : 'Unreachable'}
            </Badge>
          ) : (
            <Badge>Not checked yet</Badge>
          )}
          <Button variant="ghost" onClick={() => check.mutate(d.id)} disabled={check.isPending} title="Check now" aria-label="Check now">
            <RefreshCw className={cn('size-4', check.isPending && 'animate-spin')} />
          </Button>
          <Button
            variant="ghost"
            onClick={() => maintenance.mutate(d.id)}
            disabled={maintenance.isPending}
            title="Run maintenance now (prune + integrity check). Runs weekly on its own."
            aria-label="Run maintenance"
          >
            <Wrench className="size-4" />
          </Button>
          <Button variant="ghost" onClick={() => setShowKey(true)} title="Recovery key" aria-label="Show recovery key">
            <KeyRound className="size-4" />
          </Button>
          <Button variant="ghost" onClick={() => setConfirmDelete(true)} title="Remove" aria-label="Remove destination">
            <Trash2 className="size-4" />
          </Button>
        </div>
      </div>

      {maintenance.isSuccess && <p className="mt-3 text-xs text-muted">Maintenance queued. Follow it on the Activity page.</p>}
      {maintenance.error && <p className="mt-3 text-xs text-danger">{maintenance.error.message}</p>}
      {status && !status.reachable && (
        <div className="mt-4 flex items-start gap-2 rounded-xl bg-danger/10 px-3 py-2 text-sm text-danger">
          <TriangleAlert className="mt-0.5 size-4 shrink-0" /> {status.error}
        </div>
      )}

      <div className="mt-5 grid grid-cols-3 gap-4 border-t border-line pt-4">
        <Stat label="Snapshots" value={status?.snapshots ?? '—'} />
        <Stat label="Stored" value={formatBytes(status?.storedBytes)} hint="after deduplication and compression" />
        <Stat label="Last backup" value={relativeTime(status?.lastSnapshotAt ?? null)} />
      </div>

      <RecoveryKeyDialog destination={d} open={showKey} onClose={() => setShowKey(false)} />
      <Dialog
        open={confirmDelete}
        onClose={() => setConfirmDelete(false)}
        title={`Remove ${d.name}?`}
        description="DockerBackup forgets this destination. The backup data at the location is not deleted. You can reconnect later with its recovery key."
        footer={
          <>
            <Button onClick={() => setConfirmDelete(false)}>Keep it</Button>
            <Button variant="danger" disabled={remove.isPending} onClick={() => remove.mutate(d.id, { onSuccess: () => setConfirmDelete(false) })}>
              Remove destination
            </Button>
          </>
        }
      >
        {remove.error && <p className="text-sm text-danger">{remove.error.message}</p>}
      </Dialog>
    </Card>
  )
}

function RecoveryKeyDialog({ destination, open, onClose }: { destination: Destination; open: boolean; onClose: () => void }) {
  const [key, setKey] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  const close = () => {
    setKey(null)
    setError(null)
    setCopied(false)
    onClose()
  }

  return (
    <Dialog
      open={open}
      onClose={close}
      title="Recovery key"
      description="Every backup in this destination is encrypted with this key. Without it, the backups cannot be restored by anyone, including you on a new machine."
      footer={<Button onClick={close}>Done</Button>}
    >
      <div className="rounded-xl border border-warn/40 bg-warn/10 p-3 text-sm">
        Store it somewhere safe and separate from this computer, such as a password manager.
      </div>
      <div className="mt-4">
        {key ? (
          <div className="flex items-center gap-2">
            <code className="min-w-0 flex-1 break-all rounded-xl border border-line bg-surface px-3 py-2 font-mono text-sm">{key}</code>
            <Button
              onClick={async () => {
                await navigator.clipboard.writeText(key)
                setCopied(true)
              }}
              aria-label="Copy recovery key"
            >
              <Copy className="size-4" /> {copied ? 'Copied' : 'Copy'}
            </Button>
          </div>
        ) : (
          <Button
            variant="primary"
            onClick={() =>
              revealRecoveryKey(destination.id)
                .then(setKey)
                .catch((e: Error) => setError(e.message))
            }
          >
            <Eye className="size-4" /> Reveal key
          </Button>
        )}
        {error && <p className="mt-2 text-sm text-danger">{error}</p>}
      </div>
    </Dialog>
  )
}

function AddDestinationDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const create = useCreateDestination()
  const [kind, setKind] = useState<DestinationKind>('local')
  const [connectExisting, setConnectExisting] = useState(false)
  const [form, setForm] = useState({ name: '', path: '', smbUsername: '', smbPassword: '', smbDomain: '', existingRepositoryPassword: '' })
  const set = (field: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [field]: e.target.value }))

  const close = () => {
    create.reset()
    setForm({ name: '', path: '', smbUsername: '', smbPassword: '', smbDomain: '', existingRepositoryPassword: '' })
    setConnectExisting(false)
    onClose()
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    create.mutate(
      {
        name: form.name,
        kind,
        path: form.path,
        smbUsername: kind === 'smb' ? form.smbUsername : undefined,
        smbPassword: kind === 'smb' ? form.smbPassword : undefined,
        smbDomain: kind === 'smb' ? form.smbDomain : undefined,
        existingRepositoryPassword: connectExisting ? form.existingRepositoryPassword : undefined,
      },
      { onSuccess: close },
    )
  }

  return (
    <Dialog open={open} onClose={close} title="Add destination" description="Where should backups be stored?">
      <form onSubmit={submit} className="space-y-4">
        <div className="grid grid-cols-2 gap-2" role="radiogroup" aria-label="Destination type">
          {(
            [
              { id: 'local', icon: FolderClosed, title: 'Local folder', text: 'Another disk or a USB drive' },
              { id: 'smb', icon: Network, title: 'Network share', text: 'A NAS or another PC (SMB)' },
            ] as const
          ).map((option) => (
            <button
              key={option.id}
              type="button"
              role="radio"
              aria-checked={kind === option.id}
              onClick={() => setKind(option.id)}
              className={cn(
                'flex items-start gap-3 rounded-xl border p-3 text-left transition',
                kind === option.id ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted',
              )}
            >
              <option.icon className={cn('mt-0.5 size-4', kind === option.id ? 'text-accent' : 'text-muted')} />
              <span>
                <span className="block text-sm font-medium">{option.title}</span>
                <span className="block text-xs text-muted">{option.text}</span>
              </span>
            </button>
          ))}
        </div>

        <Field label="Name">
          <TextInput required value={form.name} onChange={set('name')} placeholder={kind === 'smb' ? 'NAS' : 'Backup disk'} autoFocus />
        </Field>
        <Field
          label={kind === 'smb' ? 'Share path' : 'Folder'}
          hint={kind === 'smb' ? 'For example \\\\nas\\backups\\docker' : 'For example D:\\Backups\\docker. It will be created if needed.'}
        >
          <TextInput required value={form.path} onChange={set('path')} className="font-mono" placeholder={kind === 'smb' ? '\\\\nas\\backups\\docker' : 'D:\\Backups\\docker'} />
        </Field>

        {kind === 'smb' && (
          <div className="grid grid-cols-2 gap-3">
            <Field label="Username" hint="Leave empty for guest access">
              <TextInput value={form.smbUsername} onChange={set('smbUsername')} autoComplete="off" />
            </Field>
            <Field label="Password">
              <TextInput type="password" value={form.smbPassword} onChange={set('smbPassword')} autoComplete="new-password" />
            </Field>
            <Field label="Domain (optional)">
              <TextInput value={form.smbDomain} onChange={set('smbDomain')} autoComplete="off" />
            </Field>
          </div>
        )}

        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={connectExisting} onChange={(e) => setConnectExisting(e.target.checked)} className="accent-[var(--accent)]" />
          This location already has DockerBackup backups (connect with its recovery key)
        </label>
        {connectExisting && (
          <Field label="Recovery key">
            <TextInput required type="password" value={form.existingRepositoryPassword} onChange={set('existingRepositoryPassword')} className="font-mono" />
          </Field>
        )}

        {create.error && (
          <div className="flex items-start gap-2 rounded-xl bg-danger/10 px-3 py-2 text-sm text-danger">
            <TriangleAlert className="mt-0.5 size-4 shrink-0" /> {create.error.message}
          </div>
        )}

        <div className="flex items-center justify-between gap-3 pt-2">
          <span className="flex items-center gap-1.5 text-xs text-muted">
            <ShieldCheck className="size-3.5" /> Encrypted with a key only this computer holds
          </span>
          <div className="flex gap-2">
            <Button onClick={close}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={create.isPending}>
              {create.isPending ? 'Setting up…' : connectExisting ? 'Connect' : 'Create'}
            </Button>
          </div>
        </div>
      </form>
    </Dialog>
  )
}
