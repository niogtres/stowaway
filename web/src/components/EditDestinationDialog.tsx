import { TriangleAlert } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { DestinationFields } from '@/components/DestinationFields'
import { SignInConnect, type SignIn } from '@/components/SignInConnect'
import { Button, Dialog, Field, TextInput } from '@/components/forms'
import { useDestinationTypes, useUpdateDestination, type Destination, type DestinationType, type UpdateDestination } from '@/lib/api'
import { splitValues, typeOf, type Values } from '@/lib/destinations'

/** The part of a cloud drive folder under /Stowaway, or null when the destination lives outside it (older versions). */
function stowawaySubfolder(prefix: string | undefined): string | null {
  const p = (prefix ?? '').replace(/^\/+|\/+$/g, '')
  if (p.toLowerCase() === 'stowaway') return ''
  return p.toLowerCase().startsWith('stowaway/') ? p.slice('stowaway/'.length) : null
}

/** Mounted only while open, so state starts fresh each time. */
export function EditDestinationDialog({ destination: d, onClose }: { destination: Destination; onClose: () => void }) {
  const type = typeOf(useDestinationTypes().data, d.kind)
  return (
    <Dialog open onClose={onClose} title={`Edit ${d.name}`} description={<span className="font-mono">{d.path}</span>} className="w-[min(42rem,calc(100vw-2rem))]">
      {type ? <EditForm destination={d} type={type} onClose={onClose} /> : <p className="text-sm text-muted">Loading…</p>}
    </Dialog>
  )
}

function EditForm({ destination: d, type, onClose }: { destination: Destination; type: DestinationType; onClose: () => void }) {
  const update = useUpdateDestination(d.id)
  const [name, setName] = useState(d.name)
  const [values, setValues] = useState<Values>(() => {
    const initial: Values = { ...d.settings }
    // Cloud drive folders are typed as a subfolder of /Stowaway.
    for (const f of type.fields.filter((f) => f.type === 'stowawayFolder')) initial[f.key] = stowawaySubfolder(d.settings[f.key]) ?? ''
    return initial
  })
  const [moveOpen, setMoveOpen] = useState(false)
  const [credentialsOpen, setCredentialsOpen] = useState(false)
  const [signIn, setSignIn] = useState<SignIn | null>(null)

  const location = type.fields.filter((f) => f.section === 'location' && !f.hidden)
  const credentials = type.fields.filter((f) => f.section === 'credentials' && !f.hidden)
  const outsideStowaway = location.some((f) => f.type === 'stowawayFolder' && stowawaySubfolder(d.settings[f.key]) === null)
  const set = (key: string, value: string) => setValues((v) => ({ ...v, [key]: value }))

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const body: UpdateDestination = {}
    if (name.trim() !== d.name) body.name = name
    const sections = new Set([...(moveOpen ? ['location'] : []), ...(credentialsOpen ? ['credentials'] : [])])
    const { settings, secrets } = splitValues(type.fields, values, (f) => sections.has(f.section))
    if (Object.keys(settings).length > 0) body.settings = settings
    if (Object.keys(secrets).length > 0) body.secrets = secrets
    if (credentialsOpen && signIn?.state === 'ready') body.signInSession = signIn.id
    if (Object.keys(body).length === 0) return onClose()
    update.mutate(body, { onSuccess: onClose })
  }

  const waitingForSignIn = credentialsOpen && type.signIn !== null && signIn !== null && signIn.state !== 'ready'

  return (
    <form onSubmit={submit} className="space-y-4">
      <Field label="Name">
        <TextInput required value={name} onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>

      <Section
        title="Location"
        open={moveOpen}
        onOpen={() => setMoveOpen(true)}
        action="Change location…"
        summary="Moved the backups (for example to another folder in Dropbox or on the NAS)? Point the destination at the new place. Stowaway checks that the same backups are there, so this can't swap in different ones."
      >
        {outsideStowaway && (
          <p className="text-xs text-muted">
            This destination is at {d.path}, outside /Stowaway. Move its folders (config, data, index, keys, locks, snapshots) into /Stowaway or a subfolder of it in {type.label} first, then enter that
            subfolder here.
          </p>
        )}
        <DestinationFields fields={location} values={values} onChange={set} />
      </Section>

      {(credentials.length > 0 || type.signIn) && (
        <Section
          title={type.signIn ? `${type.label} account` : 'Credentials'}
          open={credentialsOpen}
          onOpen={() => setCredentialsOpen(true)}
          action={type.signIn ? 'Reconnect…' : 'Change credentials…'}
          summary={type.signIn ? 'Sign in again if access was revoked. Use the account that holds these backups.' : 'Replace a changed password or key. Fields left empty keep their current value.'}
        >
          {type.signIn && <SignInConnect service={type.signIn} serviceLabel={type.label} value={signIn} onChange={setSignIn} label={`Reconnect ${type.label}`} />}
          <DestinationFields fields={credentials} values={values} onChange={set} keepSecrets />
        </Section>
      )}

      {update.error && (
        <div className="flex items-start gap-2 rounded-xl bg-danger/10 px-3 py-2 text-sm text-danger">
          <TriangleAlert className="mt-0.5 size-4 shrink-0" /> {update.error.message}
        </div>
      )}

      <div className="flex justify-end gap-2 pt-2">
        <Button onClick={onClose}>Cancel</Button>
        <Button type="submit" variant="primary" disabled={update.isPending || waitingForSignIn}>
          {update.isPending ? (moveOpen || credentialsOpen ? 'Checking…' : 'Saving…') : 'Save'}
        </Button>
      </div>
    </form>
  )
}

function Section({ title, open, onOpen, action, summary, children }: { title: string; open: boolean; onOpen: () => void; action: string; summary: string; children: ReactNode }) {
  return (
    <div className="rounded-xl border border-line p-4">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h4 className="text-sm font-medium">{title}</h4>
          <p className="mt-0.5 text-xs text-muted">{summary}</p>
        </div>
        {!open && (
          <Button type="button" onClick={onOpen} className="shrink-0">
            {action}
          </Button>
        )}
      </div>
      {open && <div className="mt-3 space-y-3">{children}</div>}
    </div>
  )
}
