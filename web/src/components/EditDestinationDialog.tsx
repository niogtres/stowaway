import { TriangleAlert } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { DropboxConnect, DropboxFolderInput, type DropboxSignIn } from '@/components/DropboxConnect'
import { Button, Dialog, Field, TextInput } from '@/components/forms'
import { useUpdateDestination, type Destination, type UpdateDestination } from '@/lib/api'

type Values = Record<string, string>

/** Settings that make up the location of each cloud type (the rest of the settings aren't editable). */
const locationKeys: Partial<Record<Destination['kind'], string[]>> = {
  s3: ['endpoint', 'bucket', 'prefix', 'region'],
  b2: ['bucket', 'prefix'],
  azure: ['accountName', 'bucket', 'prefix'],
  sftp: ['host', 'port', 'user', 'path'],
  dropbox: ['prefix'],
}

const secretKeys: Partial<Record<Destination['kind'], [string, string][]>> = {
  s3: [['accessKeyId', 'Access key ID'], ['secretAccessKey', 'Secret access key']],
  b2: [['accountId', 'Application key ID'], ['accountKey', 'Application key']],
  azure: [['accountKey', 'Account key']],
}

/** The part of a Dropbox folder under /Stowaway, or null when the destination lives outside it (older versions). */
function dropboxSubfolder(prefix: string | undefined): string | null {
  const p = (prefix ?? '').replace(/^\/+|\/+$/g, '')
  if (p.toLowerCase() === 'stowaway') return ''
  return p.toLowerCase().startsWith('stowaway/') ? p.slice('stowaway/'.length) : null
}

/** Mounted only while open, so state starts fresh each time. */
export function EditDestinationDialog({ destination: d, onClose }: { destination: Destination; onClose: () => void }) {
  const update = useUpdateDestination(d.id)
  const settings = d.settings ?? {}
  const outsideStowaway = d.kind === 'dropbox' && dropboxSubfolder(settings.prefix) === null
  const [v, setV] = useState<Values>(() => ({
    name: d.name,
    path: d.kind === 'local' || d.kind === 'smb' ? d.path : '',
    smbUsername: d.smbUsername ?? '',
    smbDomain: d.smbDomain ?? '',
    ...settings,
    prefix: d.kind === 'dropbox' ? (dropboxSubfolder(settings.prefix) ?? '') : (settings.prefix ?? ''),
  }))
  const [moveOpen, setMoveOpen] = useState(false)
  const [credentialsOpen, setCredentialsOpen] = useState(false)
  const [dropbox, setDropbox] = useState<DropboxSignIn | null>(null)
  const set = (key: string) => (e: { target: { value: string } }) => setV((x) => ({ ...x, [key]: e.target.value }))
  const val = (key: string) => v[key] ?? ''

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const body: UpdateDestination = {}
    if (val('name').trim() !== d.name) body.name = val('name')
    if (moveOpen) {
      if (d.kind === 'local' || d.kind === 'smb') body.path = val('path')
      else body.settings = Object.fromEntries((locationKeys[d.kind] ?? []).map((k) => [k, val(k)]))
    }
    if (credentialsOpen) {
      if (d.kind === 'smb') {
        body.smbUsername = val('smbUsername')
        body.smbDomain = val('smbDomain')
        if (val('smbPassword')) body.smbPassword = val('smbPassword')
      } else if (d.kind === 'sftp') {
        if (val('privateKey')) body.secrets = { privateKey: val('privateKey') }
      } else if (d.kind === 'dropbox') {
        if (dropbox?.state === 'ready') body.dropboxSession = dropbox.id
      } else {
        body.secrets = Object.fromEntries((secretKeys[d.kind] ?? []).filter(([k]) => val(k)).map(([k]) => [k, val(k)]))
      }
    }
    if (Object.keys(body).length === 0) return onClose()
    update.mutate(body, { onSuccess: onClose })
  }

  const waitingForDropbox = credentialsOpen && d.kind === 'dropbox' && dropbox !== null && dropbox.state !== 'ready'

  return (
    <Dialog open onClose={onClose} title={`Edit ${d.name}`} description={<span className="font-mono">{d.path}</span>} className="w-[min(42rem,calc(100vw-2rem))]">
      <form onSubmit={submit} className="space-y-4">
        <Field label="Name">
          <TextInput required value={val('name')} onChange={set('name')} autoFocus />
        </Field>

        <Section
          title="Location"
          open={moveOpen}
          onOpen={() => setMoveOpen(true)}
          action="Change location…"
          summary="Moved the backups (for example to another folder in Dropbox or on the NAS)? Point the destination at the new place. Stowaway checks that the same backups are there, so this can't swap in different ones."
        >
          {(d.kind === 'local' || d.kind === 'smb') && (
            <Field label={d.kind === 'local' ? 'Folder' : 'Share path'}>
              <TextInput required value={val('path')} onChange={set('path')} className="font-mono" />
            </Field>
          )}
          {d.kind === 'dropbox' && (
            <Field
              label="Folder in Dropbox"
              hint={
                outsideStowaway
                  ? `This destination is at ${d.path}, outside /Stowaway. Move its folders (config, data, index, keys, locks, snapshots) into /Stowaway or a subfolder of it in Dropbox first, then enter that subfolder here.`
                  : 'Move the folder in Dropbox first, then enter where it is now.'
              }
            >
              <DropboxFolderInput value={val('prefix')} onChange={(x) => setV((o) => ({ ...o, prefix: x }))} />
            </Field>
          )}
          {(d.kind === 's3' || d.kind === 'b2' || d.kind === 'azure' || d.kind === 'sftp') && (
            <div className="grid grid-cols-2 gap-3">
              {d.kind === 's3' && <Labelled label="Endpoint" value={val('endpoint')} onChange={set('endpoint')} />}
              {d.kind === 'azure' && <Labelled label="Storage account" value={val('accountName')} onChange={set('accountName')} />}
              {d.kind !== 'sftp' && <Labelled label={d.kind === 'azure' ? 'Container' : 'Bucket'} value={val('bucket')} onChange={set('bucket')} />}
              {d.kind !== 'sftp' && <Labelled label="Folder (optional)" value={val('prefix')} onChange={set('prefix')} optional />}
              {d.kind === 's3' && <Labelled label="Region (optional)" value={val('region')} onChange={set('region')} optional />}
              {d.kind === 'sftp' && (
                <>
                  <Labelled label="Server" value={val('host')} onChange={set('host')} />
                  <Labelled label="Port" value={val('port')} onChange={set('port')} optional />
                  <Labelled label="User" value={val('user')} onChange={set('user')} />
                  <Labelled label="Folder on the server" value={val('path')} onChange={set('path')} />
                </>
              )}
            </div>
          )}
        </Section>

        {d.kind !== 'local' && (
          <Section
            title={d.kind === 'dropbox' ? 'Dropbox account' : 'Credentials'}
            open={credentialsOpen}
            onOpen={() => setCredentialsOpen(true)}
            action={d.kind === 'dropbox' ? 'Reconnect…' : 'Change credentials…'}
            summary={d.kind === 'dropbox' ? 'Sign in again if Dropbox access was revoked. Use the account that holds these backups.' : 'Replace a changed password or key. Fields left empty keep their current value.'}
          >
            {d.kind === 'dropbox' && <DropboxConnect value={dropbox} onChange={setDropbox} label="Reconnect Dropbox" />}
            {d.kind === 'smb' && (
              <div className="grid grid-cols-2 gap-3">
                <Labelled label="Username" value={val('smbUsername')} onChange={set('smbUsername')} optional />
                <Labelled label="Domain (optional)" value={val('smbDomain')} onChange={set('smbDomain')} optional />
                <Labelled label="New password" value={val('smbPassword')} onChange={set('smbPassword')} optional secret />
              </div>
            )}
            {d.kind === 'sftp' && (
              <Field label="New SSH private key">
                <textarea
                  value={val('privateKey')}
                  onChange={set('privateKey')}
                  rows={4}
                  spellCheck={false}
                  className="w-full rounded-xl border border-line bg-surface-strong px-3 py-2 font-mono text-xs outline-none focus:border-accent"
                  placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
                />
              </Field>
            )}
            {secretKeys[d.kind] && (
              <div className="grid grid-cols-2 gap-3">
                {secretKeys[d.kind]!.map(([k, label]) => (
                  <Labelled key={k} label={label} value={val(k)} onChange={set(k)} optional secret />
                ))}
              </div>
            )}
          </Section>
        )}

        {update.error && (
          <div className="flex items-start gap-2 rounded-xl bg-danger/10 px-3 py-2 text-sm text-danger">
            <TriangleAlert className="mt-0.5 size-4 shrink-0" /> {update.error.message}
          </div>
        )}

        <div className="flex justify-end gap-2 pt-2">
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={update.isPending || waitingForDropbox}>
            {update.isPending ? (moveOpen || credentialsOpen ? 'Checking…' : 'Saving…') : 'Save'}
          </Button>
        </div>
      </form>
    </Dialog>
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

function Labelled({ label, value, onChange, optional, secret }: { label: string; value: string; onChange: (e: { target: { value: string } }) => void; optional?: boolean; secret?: boolean }) {
  return (
    <Field label={label}>
      <TextInput
        required={!optional}
        type={secret ? 'password' : undefined}
        autoComplete={secret ? 'new-password' : 'off'}
        value={value}
        onChange={onChange}
        className={secret ? undefined : 'font-mono'}
      />
    </Field>
  )
}
