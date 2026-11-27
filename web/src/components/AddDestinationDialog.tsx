import { Check, ExternalLink, ShieldCheck, TriangleAlert } from 'lucide-react'
import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { Button, Dialog, Field, TextInput } from '@/components/forms'
import { destinationKinds } from '@/lib/destinations'
import { pollDropbox, startDropbox, useCreateDestination, type DestinationKind } from '@/lib/api'
import { cn } from '@/lib/utils'

type Values = Record<string, string>

const placeholders: Partial<Record<DestinationKind, string>> = { local: 'Backup disk', smb: 'NAS', s3: 'S3', b2: 'Backblaze', azure: 'Azure', sftp: 'Offsite server', dropbox: 'Dropbox' }

/** Mounted only while open (see Destinations page), so state starts fresh each time. */
export function AddDestinationDialog({ onClose }: { onClose: () => void }) {
  const create = useCreateDestination()
  const [kind, setKind] = useState<DestinationKind>('local')
  const [v, setV] = useState<Values>({ endpoint: 'https://s3.amazonaws.com', port: '22' })
  const [connectExisting, setConnectExisting] = useState(false)
  const [dropbox, setDropbox] = useState<{ id: string; state: 'pending' | 'ready' | 'failed'; error: string | null } | null>(null)
  const [dropboxStarting, setDropboxStarting] = useState(false)
  const set = (key: string) => (e: { target: { value: string } }) => setV((x) => ({ ...x, [key]: e.target.value }))
  const val = (key: string) => v[key] ?? ''

  // Poll the Dropbox sign-in until the browser flow completes.
  useEffect(() => {
    if (!dropbox || dropbox.state !== 'pending') return
    const timer = setInterval(() => {
      pollDropbox(dropbox.id)
        .then((s) => setDropbox({ id: s.id, state: s.state, error: s.error }))
        .catch(() => setDropbox((d) => (d ? { ...d, state: 'failed', error: 'The sign-in session expired.' } : d)))
    }, 1500)
    return () => clearInterval(timer)
  }, [dropbox])

  const connectDropbox = async () => {
    setDropboxStarting(true)
    try {
      const session = await startDropbox()
      setDropbox({ id: session.id, state: session.state, error: session.error })
      if (session.authUrl) window.open(session.authUrl, '_blank', 'noopener')
    } catch (e) {
      setDropbox({ id: '', state: 'failed', error: (e as Error).message })
    } finally {
      setDropboxStarting(false)
    }
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const settings: Values = {}
    const secrets: Values = {}
    const pick = (target: Values, ...keys: string[]) => keys.forEach((k) => val(k) && (target[k] = val(k)))
    switch (kind) {
      case 's3':
        pick(settings, 'endpoint', 'bucket', 'prefix', 'region')
        pick(secrets, 'accessKeyId', 'secretAccessKey')
        break
      case 'b2':
        pick(settings, 'bucket', 'prefix')
        pick(secrets, 'accountId', 'accountKey')
        break
      case 'azure':
        pick(settings, 'accountName', 'bucket', 'prefix')
        pick(secrets, 'accountKey')
        break
      case 'sftp':
        pick(settings, 'host', 'port', 'user', 'path')
        pick(secrets, 'privateKey')
        break
      case 'dropbox':
        pick(settings, 'prefix')
        break
    }
    create.mutate(
      {
        name: val('name'),
        kind,
        path: val('path'),
        smbUsername: kind === 'smb' ? val('smbUsername') : undefined,
        smbPassword: kind === 'smb' ? val('smbPassword') : undefined,
        smbDomain: kind === 'smb' ? val('smbDomain') : undefined,
        existingRepositoryPassword: connectExisting ? val('existing') : undefined,
        settings,
        secrets,
        dropboxSession: kind === 'dropbox' ? dropbox?.id : undefined,
      },
      { onSuccess: onClose },
    )
  }

  const ready = kind !== 'dropbox' || dropbox?.state === 'ready'

  return (
    <Dialog open onClose={onClose} title="Add destination" description="Where should backups be stored?" className="w-[min(42rem,calc(100vw-2rem))]">
      <form onSubmit={submit} className="space-y-4">
        <div className="grid grid-cols-4 gap-2" role="radiogroup" aria-label="Destination type">
          {destinationKinds.map((o) => (
            <button
              key={o.id}
              type="button"
              role="radio"
              aria-checked={kind === o.id}
              onClick={() => setKind(o.id)}
              className={cn('flex flex-col items-start gap-1 rounded-xl border p-2.5 text-left transition', kind === o.id ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted')}
            >
              <o.icon className={cn('size-4', kind === o.id ? 'text-accent' : 'text-muted')} />
              <span className="text-xs font-medium">{o.label}</span>
            </button>
          ))}
        </div>

        <Field label="Name">
          <TextInput required value={val('name')} onChange={set('name')} placeholder={placeholders[kind]} autoFocus />
        </Field>

        {kind === 'local' && (
          <Field label="Folder" hint="For example D:\Backups\docker. It will be created if needed.">
            <TextInput required value={val('path')} onChange={set('path')} className="font-mono" placeholder="D:\Backups\docker" />
          </Field>
        )}

        {kind === 'smb' && (
          <>
            <Field label="Share path" hint="For example \\nas\backups\docker">
              <TextInput required value={val('path')} onChange={set('path')} className="font-mono" placeholder="\\nas\backups\docker" />
            </Field>
            <Grid>
              <Field label="Username" hint="Leave empty for guest access">
                <TextInput value={val('smbUsername')} onChange={set('smbUsername')} autoComplete="off" />
              </Field>
              <Field label="Password">
                <TextInput type="password" value={val('smbPassword')} onChange={set('smbPassword')} autoComplete="new-password" />
              </Field>
              <Field label="Domain (optional)">
                <TextInput value={val('smbDomain')} onChange={set('smbDomain')} autoComplete="off" />
              </Field>
            </Grid>
          </>
        )}

        {kind === 's3' && (
          <>
            <Field label="Endpoint" hint="AWS: https://s3.amazonaws.com. Also works with MinIO, Wasabi, Cloudflare R2 and other S3-compatible services.">
              <TextInput required value={val('endpoint')} onChange={set('endpoint')} className="font-mono" />
            </Field>
            <Grid>
              <Field label="Bucket">
                <TextInput required value={val('bucket')} onChange={set('bucket')} className="font-mono" />
              </Field>
              <Field label="Folder in bucket (optional)">
                <TextInput value={val('prefix')} onChange={set('prefix')} className="font-mono" placeholder="docker" />
              </Field>
              <Field label="Access key ID">
                <TextInput required value={val('accessKeyId')} onChange={set('accessKeyId')} autoComplete="off" />
              </Field>
              <Field label="Secret access key">
                <TextInput required type="password" value={val('secretAccessKey')} onChange={set('secretAccessKey')} autoComplete="new-password" />
              </Field>
              <Field label="Region (optional)">
                <TextInput value={val('region')} onChange={set('region')} placeholder="eu-west-1" />
              </Field>
            </Grid>
          </>
        )}

        {kind === 'b2' && (
          <Grid>
            <Field label="Bucket">
              <TextInput required value={val('bucket')} onChange={set('bucket')} className="font-mono" />
            </Field>
            <Field label="Folder in bucket (optional)">
              <TextInput value={val('prefix')} onChange={set('prefix')} className="font-mono" placeholder="docker" />
            </Field>
            <Field label="Application key ID">
              <TextInput required value={val('accountId')} onChange={set('accountId')} autoComplete="off" />
            </Field>
            <Field label="Application key">
              <TextInput required type="password" value={val('accountKey')} onChange={set('accountKey')} autoComplete="new-password" />
            </Field>
          </Grid>
        )}

        {kind === 'azure' && (
          <Grid>
            <Field label="Storage account">
              <TextInput required value={val('accountName')} onChange={set('accountName')} className="font-mono" />
            </Field>
            <Field label="Account key">
              <TextInput required type="password" value={val('accountKey')} onChange={set('accountKey')} autoComplete="new-password" />
            </Field>
            <Field label="Container">
              <TextInput required value={val('bucket')} onChange={set('bucket')} className="font-mono" />
            </Field>
            <Field label="Folder (optional)">
              <TextInput value={val('prefix')} onChange={set('prefix')} className="font-mono" placeholder="docker" />
            </Field>
          </Grid>
        )}

        {kind === 'sftp' && (
          <>
            <div className="grid grid-cols-[1fr_6rem_1fr] gap-3">
              <Field label="Server">
                <TextInput required value={val('host')} onChange={set('host')} className="font-mono" placeholder="backup.example.com" />
              </Field>
              <Field label="Port">
                <TextInput value={val('port')} onChange={set('port')} inputMode="numeric" />
              </Field>
              <Field label="User">
                <TextInput required value={val('user')} onChange={set('user')} className="font-mono" />
              </Field>
            </div>
            <Field label="Folder on the server">
              <TextInput required value={val('path')} onChange={set('path')} className="font-mono" placeholder="/backups/docker" />
            </Field>
            <Field label="SSH private key" hint="OpenSSH format without a passphrase. The server's identity is recorded now and checked on every connection.">
              <textarea
                required
                value={val('privateKey')}
                onChange={set('privateKey')}
                rows={4}
                spellCheck={false}
                className="w-full rounded-xl border border-line bg-surface-strong px-3 py-2 font-mono text-xs outline-none focus:border-accent"
                placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
              />
            </Field>
          </>
        )}

        {kind === 'dropbox' && (
          <>
            <div className="rounded-xl border border-line p-4">
              {dropbox?.state === 'ready' ? (
                <p className="flex items-center gap-2 text-sm text-ok">
                  <Check className="size-4" /> Dropbox connected.
                </p>
              ) : dropbox?.state === 'pending' ? (
                <p className="text-sm">
                  Finish signing in on the Dropbox page that just opened. This page updates by itself.{' '}
                  <button type="button" onClick={connectDropbox} className="text-accent hover:underline">
                    Open it again
                  </button>
                </p>
              ) : (
                <div className="flex items-center justify-between gap-3">
                  <p className="text-sm text-muted">Sign in to Dropbox and allow access. Stowaway only keeps the access it is given.</p>
                  <Button variant="primary" onClick={connectDropbox} disabled={dropboxStarting}>
                    <ExternalLink className="size-4" /> {dropboxStarting ? 'Starting…' : 'Connect Dropbox'}
                  </Button>
                </div>
              )}
              {dropbox?.state === 'failed' && <p className="mt-2 text-sm text-danger">{dropbox.error}</p>}
            </div>
            <Field label="Folder in Dropbox">
              <TextInput value={val('prefix')} onChange={set('prefix')} className="font-mono" placeholder="Stowaway" />
            </Field>
          </>
        )}

        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={connectExisting} onChange={(e) => setConnectExisting(e.target.checked)} className="accent-[var(--accent)]" />
          This location already has Stowaway backups (connect with its recovery key)
        </label>
        {connectExisting && (
          <Field label="Recovery key">
            <TextInput required type="password" value={val('existing')} onChange={set('existing')} className="font-mono" />
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
    </Dialog>
  )
}

function Grid({ children }: { children: ReactNode }) {
  return <div className="grid grid-cols-2 gap-3">{children}</div>
}
