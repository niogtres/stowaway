import { ShieldCheck, TriangleAlert } from 'lucide-react'
import { createElement, useState, type FormEvent } from 'react'
import { DestinationFields } from '@/components/DestinationFields'
import { SignInConnect, type SignIn } from '@/components/SignInConnect'
import { Button, Dialog, Field, TextInput } from '@/components/forms'
import { iconFor, splitValues, type Values } from '@/lib/destinations'
import { useCreateDestination, useDestinations, useDestinationTypes, type Destination, type DestinationKind, type DestinationType } from '@/lib/api'
import { cn } from '@/lib/utils'

/** Mounted only while open (see Destinations page), so state starts fresh each time. */
export function AddDestinationDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (created: Destination) => void }) {
  const types = useDestinationTypes().data ?? []
  const [kind, setKind] = useState<DestinationKind>('local')
  const [name, setName] = useState('')
  const type = types.find((t) => t.kind === kind)

  return (
    <Dialog open onClose={onClose} title="Add destination" description="Where should backups be stored?" className="w-[min(42rem,calc(100vw-2rem))]">
      <div className="space-y-4">
        <div className="grid grid-cols-4 gap-2" role="radiogroup" aria-label="Destination type">
          {types.map((t) => (
            <button
              key={t.kind}
              type="button"
              role="radio"
              aria-checked={kind === t.kind}
              onClick={() => setKind(t.kind)}
              className={cn('flex flex-col items-start gap-1 rounded-xl border p-2.5 text-left transition', kind === t.kind ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted')}
            >
              {createElement(iconFor(t.icon), { className: cn('size-4', kind === t.kind ? 'text-accent' : 'text-muted') })}
              <span className="text-xs font-medium">{t.label}</span>
            </button>
          ))}
        </div>
        {/* Keyed by type, so switching starts with that type's defaults. */}
        {type && <AddForm key={type.kind} type={type} name={name} setName={setName} onClose={onClose} onCreated={onCreated} />}
      </div>
    </Dialog>
  )
}

function AddForm({
  type,
  name,
  setName,
  onClose,
  onCreated,
}: {
  type: DestinationType
  name: string
  setName: (name: string) => void
  onClose: () => void
  onCreated: (created: Destination) => void
}) {
  const create = useCreateDestination()
  const [values, setValues] = useState<Values>(() => Object.fromEntries(type.fields.filter((f) => f.default).map((f) => [f.key, f.default!])))
  const [connectExisting, setConnectExisting] = useState(false)
  const [existingKey, setExistingKey] = useState('')
  const [signIn, setSignIn] = useState<SignIn | null>(null)

  // Types with a browser sign-in can reuse the sign-in of a destination of the same type.
  const sameType = (useDestinations().data ?? []).filter((d) => d.kind === type.kind)
  const [reuseChoice, setReuseChoice] = useState<string | null>(null)
  const reuseFrom = type.signIn ? (reuseChoice ?? sameType[0]?.id ?? '') : ''

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const { settings, secrets } = splitValues(type.fields, values)
    create.mutate(
      {
        name,
        kind: type.kind,
        settings,
        secrets,
        existingRepositoryPassword: connectExisting ? existingKey : undefined,
        signInSession: type.signIn && !reuseFrom ? signIn?.id : undefined,
        credentialsFrom: reuseFrom || undefined,
      },
      { onSuccess: onCreated },
    )
  }

  const ready = !type.signIn || !!reuseFrom || signIn?.state === 'ready'

  return (
    <form onSubmit={submit} className="space-y-4">
      <Field label="Name">
        <TextInput required value={name} onChange={(e) => setName(e.target.value)} placeholder={type.namePlaceholder} autoFocus />
      </Field>

      {type.signIn && (
        <>
          {sameType.length > 0 && (
            <Field label={`${type.label} account`} hint={`Reuse the account of a ${type.label} destination you already have, or connect another one.`}>
              <select
                value={reuseFrom}
                onChange={(e) => setReuseChoice(e.target.value)}
                className="w-full rounded-xl border border-line bg-surface-strong px-3 py-2 text-sm outline-none focus:border-accent"
              >
                {sameType.map((d) => (
                  <option key={d.id} value={d.id}>
                    Same account as {d.name} ({d.path})
                  </option>
                ))}
                <option value="">Connect another {type.label} account</option>
              </select>
            </Field>
          )}
          {!reuseFrom && <SignInConnect service={type.signIn} serviceLabel={type.label} value={signIn} onChange={setSignIn} />}
        </>
      )}

      <DestinationFields fields={type.fields} values={values} onChange={(key, value) => setValues((v) => ({ ...v, [key]: value }))} />
      {type.notes && <p className="text-xs text-muted">{type.notes}</p>}

      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={connectExisting} onChange={(e) => setConnectExisting(e.target.checked)} className="accent-[var(--accent)]" />
        This location already has Stowaway backups (connect with its recovery key)
      </label>
      {connectExisting && (
        <Field label="Recovery key">
          <TextInput required type="password" value={existingKey} onChange={(e) => setExistingKey(e.target.value)} className="font-mono" />
        </Field>
      )}

      {create.error && (
        <div className="flex items-start gap-2 rounded-xl bg-danger/10 px-3 py-2 text-sm text-danger">
          <TriangleAlert className="mt-0.5 size-4 shrink-0" /> {create.error.message}
        </div>
      )}

      <div className="flex items-center justify-between gap-3 pt-2">
        <span className="flex items-center gap-1.5 text-xs text-muted">
          <ShieldCheck className="size-3.5" /> Encrypted before it leaves this computer
        </span>
        <div className="flex gap-2">
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={create.isPending || !ready}>
            {create.isPending ? 'Setting up…' : connectExisting ? 'Connect' : 'Create'}
          </Button>
        </div>
      </div>
    </form>
  )
}
