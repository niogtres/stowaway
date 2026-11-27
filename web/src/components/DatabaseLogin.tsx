import { Check, KeyRound, LoaderCircle, X } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { databaseLabels, useDatabaseCredentialActions, useDatabaseCredentials, type ContainerInfo } from '@/lib/api'
import { cn } from '@/lib/utils'
import { Button, Dialog, Field, TextInput } from './forms'

const dumpableDatabases = new Set(['postgreSql', 'mySql', 'mariaDb', 'mongoDb', 'redis'])

/** Button on a database container: shows whether the dump logs in automatically or with saved credentials. */
export function DatabaseLoginButton({ container }: { container: ContainerInfo }) {
  const credentials = useDatabaseCredentials()
  const [open, setOpen] = useState(false)
  if (!container.database || !dumpableDatabases.has(container.database)) return null
  const saved = credentials.data?.find((c) => c.containerName === container.name)

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[11px] text-muted transition hover:bg-accent-soft hover:text-fg"
        title="How the backup logs in to this database for its dump"
      >
        <KeyRound className="size-3" />
        {saved ? `Login: ${saved.user}` : 'Login: automatic'}
      </button>
      <DatabaseLoginDialog container={container} open={open} onClose={() => setOpen(false)} />
    </>
  )
}

function DatabaseLoginDialog({ container, open, onClose }: { container: ContainerInfo; open: boolean; onClose: () => void }) {
  const credentials = useDatabaseCredentials()
  const actions = useDatabaseCredentialActions()
  const saved = credentials.data?.find((c) => c.containerName === container.name)
  const [user, setUser] = useState('')
  const [password, setPassword] = useState('')
  const [database, setDatabase] = useState('')
  const [busy, setBusy] = useState<'test' | 'save' | 'auto' | null>(null)
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null)
  const [prevOpen, setPrevOpen] = useState(false)

  if (open !== prevOpen) {
    setPrevOpen(open)
    if (open) {
      setUser(saved?.user ?? '')
      setPassword('')
      setDatabase(saved?.database ?? '')
      setResult(null)
    }
  }

  const kind = databaseLabels[container.database!]
  const run = async (what: 'test' | 'save' | 'auto', action: () => Promise<void>) => {
    setBusy(what)
    setResult(null)
    try {
      await action()
    } catch (e) {
      setResult({ ok: false, text: e instanceof Error ? e.message : 'That didn’t work.' })
    } finally {
      setBusy(null)
    }
  }

  const test = (entered: boolean) =>
    run('test', async () => {
      const r = await actions.test(container.name, entered ? { user, password, database } : undefined)
      setResult(
        r.ok
          ? { ok: true, text: entered ? 'These credentials work.' : saved ? 'The saved credentials work.' : 'Automatic login works: nothing to set.' }
          : { ok: false, text: r.error ?? 'The login failed.' },
      )
    })

  const save = (e: FormEvent) => {
    e.preventDefault()
    void run('save', async () => {
      await actions.save(container.name, { user, password, database })
      onClose()
    })
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={`${kind} login for ${container.name}`}
      description={
        saved
          ? `Backups log in as “${saved.user}” with the password saved in Stowaway.`
          : `Backups log in with what the container's own settings say (the image's environment variables or Docker secrets). Set credentials here if that doesn't work, for example when the password was changed after setup.`
      }
    >
      <form onSubmit={save} className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="User">
            <TextInput required value={user} onChange={(e) => setUser(e.target.value)} placeholder={container.database === 'postgreSql' ? 'postgres' : 'root'} autoComplete="off" />
          </Field>
          <Field label="Password" hint={saved ? 'Leave empty to keep the saved one' : undefined}>
            <TextInput type="password" value={password} onChange={(e) => setPassword(e.target.value)} required={!saved} autoComplete="new-password" />
          </Field>
        </div>
        {container.database !== 'redis' && (
          <Field label="Database (optional)" hint="Leave empty to back up every database this user can see.">
            <TextInput value={database} onChange={(e) => setDatabase(e.target.value)} autoComplete="off" />
          </Field>
        )}

        {result && (
          <p className={cn('flex items-start gap-1.5 text-sm', result.ok ? 'text-ok' : 'text-danger')}>
            {result.ok ? <Check className="mt-0.5 size-4 shrink-0" /> : <X className="mt-0.5 size-4 shrink-0" />}
            {result.text}
          </p>
        )}

        <div className="flex flex-wrap items-center justify-between gap-2 pt-2">
          <div className="flex gap-2">
            <Button onClick={() => test(false)} disabled={busy !== null} title="Check the login the next backup would use">
              {busy === 'test' && !user ? <LoaderCircle className="size-4 animate-spin" /> : null}
              Test current login
            </Button>
            {saved && (
              <Button variant="ghost" disabled={busy !== null} onClick={() => run('auto', async () => { await actions.remove(container.name); onClose() })}>
                Use automatic
              </Button>
            )}
          </div>
          <div className="flex gap-2">
            <Button onClick={() => test(true)} disabled={busy !== null || !user}>
              {busy === 'test' && user ? <LoaderCircle className="size-4 animate-spin" /> : null}
              Test these
            </Button>
            <Button type="submit" variant="primary" disabled={busy !== null}>
              Save
            </Button>
          </div>
        </div>
      </form>
    </Dialog>
  )
}
