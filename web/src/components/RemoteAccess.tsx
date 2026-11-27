import { Check, Copy, Download, Globe, KeyRound, Plus, RefreshCw, RotateCcw, Smartphone, Trash2, UserRound } from 'lucide-react'
import { useMemo, useState, type FormEvent } from 'react'
import { encode } from 'uqr'
import {
  restartService,
  useAccountActions,
  useRemoteAccess,
  useRemoteAccounts,
  useSaveRemoteAccess,
  type AccountSetup,
  type FirewallProfile,
  type RemoteAccessSettings,
  type RemoteAccessView,
  type RemoteAccount,
} from '@/lib/api'
import { cn } from '@/lib/utils'
import { Button, Dialog, Field, TextInput } from './forms'
import { Badge, Card, Skeleton, StatusDot } from './ui'

const profiles: { id: FirewallProfile; label: string; hint: string }[] = [
  { id: 'domain', label: 'Domain', hint: 'Company networks' },
  { id: 'private', label: 'Private', hint: 'Home and trusted networks' },
  { id: 'public', label: 'Public', hint: 'Not recommended' },
]

/**
 * Settings → Remote access. Managed only on this computer: from elsewhere the API answers 403 and this
 * section doesn't render at all.
 */
export function RemoteAccessSection() {
  const remote = useRemoteAccess()
  const accounts = useRemoteAccounts(remote.isSuccess)

  if (remote.isError) return null
  return (
    <section>
      <div className="mb-3">
        <h2 className="font-semibold tracking-tight">Remote access</h2>
        <p className="mt-1 text-sm text-muted">
          Manage DockerBackup from other computers on your network. Off unless you switch it on. People signing in from elsewhere use a DockerBackup account with a
          password and an authenticator app; recovery keys can only be shown on this computer.
        </p>
      </div>
      {remote.isPending ? (
        <Skeleton className="h-48 rounded-2xl" />
      ) : (
        <div className="grid gap-3">
          <StatusCard view={remote.data!} />
          <Accounts accounts={accounts.data ?? []} loading={accounts.isPending} />
          <SettingsForm key={JSON.stringify(remote.data!.settings)} view={remote.data!} readyAccounts={(accounts.data ?? []).filter((a) => a.ready).length} />
        </div>
      )}
    </section>
  )
}

function StatusCard({ view }: { view: RemoteAccessView }) {
  const [restarting, setRestarting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const tone = view.problem ? 'danger' : view.restartRequired ? 'warn' : view.active ? 'ok' : 'muted'

  return (
    <Card className="flex flex-wrap items-center gap-4 py-4">
      <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-accent-soft">
        <Globe className="size-5 text-accent" />
      </div>
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 font-medium">
          <StatusDot tone={tone} />
          {view.active ? 'On' : 'Off'}
          {view.restartRequired && <Badge tone="warn">Restart to apply</Badge>}
        </div>
        <div className="mt-0.5 text-xs text-muted">
          {view.problem ??
            (view.active
              ? view.urls.join(' · ')
              : view.restartRequired
                ? 'Your changes are saved and take effect when the service restarts.'
                : `Only this computer can open DockerBackup (https://localhost:${view.port}).`)}
        </div>
        {error && <div className="mt-1 text-xs text-danger">{error}</div>}
      </div>
      {view.restartRequired &&
        (view.canRestart ? (
          <Button
            variant="primary"
            disabled={restarting}
            onClick={async () => {
              setRestarting(true)
              setError(null)
              try {
                await restartService()
                // The service goes away for a few seconds; reload once it's back.
                for (let i = 0; i < 30; i++) {
                  await new Promise((r) => setTimeout(r, 2000))
                  if (await fetch('/api/health').then((r) => r.ok, () => false)) break
                }
                window.location.reload()
              } catch (e) {
                setError(e instanceof Error ? e.message : 'Restart failed.')
                setRestarting(false)
              }
            }}
          >
            <RefreshCw className={cn('size-4', restarting && 'animate-spin')} /> {restarting ? 'Restarting…' : 'Restart service'}
          </Button>
        ) : (
          <span className="text-xs text-muted">Restart DockerBackup to apply.</span>
        ))}
    </Card>
  )
}

function SettingsForm({ view, readyAccounts }: { view: RemoteAccessView; readyAccounts: number }) {
  const save = useSaveRemoteAccess()
  const [f, setF] = useState<RemoteAccessSettings>(view.settings)
  const [names, setNames] = useState(view.settings.hostNames.join(', '))
  const [proxies, setProxies] = useState(view.settings.trustedProxies.join(', '))
  const hostNames = names
    .split(/[\s,;]+/)
    .map((n) => n.trim())
    .filter(Boolean)
  const suggestions = view.suggestedHostNames.filter((s) => !hostNames.some((h) => h.toLowerCase() === s.toLowerCase()))
  const noCertificates = view.certificates.length === 0

  const submit = (e: FormEvent) => {
    e.preventDefault()
    save.mutate({
      ...f,
      hostNames,
      trustedProxies: proxies
        .split(/[\s,;]+/)
        .map((p) => p.trim())
        .filter(Boolean),
    })
  }

  return (
    <Card>
      <form onSubmit={submit} className="space-y-5">
        <label className="flex items-start gap-3">
          <input type="checkbox" className="mt-1 accent-[var(--accent)]" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} />
          <span>
            <span className="block text-sm font-medium">Allow access from other computers</span>
            <span className="block text-xs text-muted">
              {readyAccounts === 0 ? 'Add an account below first.' : `DockerBackup will also listen on port ${view.port} on your network.`}
            </span>
          </span>
        </label>

        <Field label="Names other computers use to reach this one" hint="Separate several with commas. Only these names are accepted, which also protects against DNS rebinding.">
          <TextInput value={names} onChange={(e) => setNames(e.target.value)} placeholder="backup-server, backup-server.home.lan, 192.168.1.20" className="font-mono" />
        </Field>
        {suggestions.length > 0 && (
          <div className="-mt-3 flex flex-wrap gap-1.5">
            {suggestions.map((s) => (
              <button
                key={s}
                type="button"
                onClick={() => setNames(hostNames.concat(s).join(', '))}
                className="rounded-lg border border-line px-2 py-0.5 font-mono text-[11px] text-muted transition hover:border-accent hover:text-fg"
              >
                + {s}
              </button>
            ))}
          </div>
        )}

        <Field
          label="Reverse proxy (optional)"
          hint="The IPv4 address of a proxy such as nginx in front of DockerBackup. Then only the proxy may connect, and the visitor address it forwards (X-Forwarded-For) is used for lockouts, rate limits and the audit log."
        >
          <TextInput value={proxies} onChange={(e) => setProxies(e.target.value)} placeholder="192.168.1.10" className="font-mono" />
        </Field>

        <fieldset>
          <legend className="mb-2 text-xs font-medium text-muted">HTTPS certificate</legend>
          <div className="grid gap-2 sm:grid-cols-2">
            <Choice
              checked={f.certificateSource === 'selfSigned'}
              onSelect={() => setF({ ...f, certificateSource: 'selfSigned', certificateThumbprint: null })}
              title="Issued by DockerBackup"
              text="Browsers warn until you trust it on each computer you use."
            />
            <Choice
              checked={f.certificateSource === 'store'}
              disabled={noCertificates}
              onSelect={() => setF({ ...f, certificateSource: 'store', certificateThumbprint: f.certificateThumbprint ?? view.certificates[0]?.thumbprint ?? null })}
              title="A certificate installed on this computer"
              text={noCertificates ? "None found in the computer's Personal store." : 'From your own certificate authority or a public one.'}
            />
          </div>
          {f.certificateSource === 'selfSigned' && view.hasSelfSignedCertificate && (
            <a href="/api/remote/certificate" className="mt-2 inline-flex items-center gap-1.5 text-xs text-accent hover:underline">
              <Download className="size-3.5" /> Download the certificate to trust it on other computers
            </a>
          )}
          {f.certificateSource === 'store' && (
            <select
              value={f.certificateThumbprint ?? ''}
              onChange={(e) => setF({ ...f, certificateThumbprint: e.target.value })}
              className="mt-2 w-full rounded-xl border border-line bg-surface-strong px-3 py-2 text-sm"
            >
              {view.certificates.map((c) => (
                <option key={c.thumbprint} value={c.thumbprint}>
                  {c.subject} · {c.names.join(', ') || 'no names'} · expires {new Date(c.notAfter).toLocaleDateString()}
                </option>
              ))}
            </select>
          )}
        </fieldset>

        <fieldset>
          <legend className="mb-2 text-xs font-medium text-muted">Allow through Windows Firewall on</legend>
          <div className="flex flex-wrap gap-x-5 gap-y-2 text-sm">
            {profiles.map((p) => (
              <label key={p.id} className="flex items-center gap-2" title={p.hint}>
                <input
                  type="checkbox"
                  className="accent-[var(--accent)]"
                  checked={f.firewallProfiles.includes(p.id)}
                  onChange={(e) =>
                    setF({ ...f, firewallProfiles: e.target.checked ? [...f.firewallProfiles, p.id] : f.firewallProfiles.filter((x) => x !== p.id) })
                  }
                />
                {p.label} <span className="text-xs text-muted">({p.hint})</span>
              </label>
            ))}
          </div>
        </fieldset>

        {save.error && <p className="text-sm text-danger">{save.error.message}</p>}
        <div className="flex justify-end">
          <Button type="submit" variant="primary" disabled={save.isPending}>
            Save
          </Button>
        </div>
      </form>
    </Card>
  )
}

function Choice({ checked, disabled, onSelect, title, text }: { checked: boolean; disabled?: boolean; onSelect: () => void; title: string; text: string }) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={checked}
      disabled={disabled}
      onClick={onSelect}
      className={cn(
        'rounded-xl border p-3 text-left transition disabled:opacity-50',
        checked ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted',
      )}
    >
      <span className="block text-sm font-medium">{title}</span>
      <span className="mt-0.5 block text-xs text-muted">{text}</span>
    </button>
  )
}

// ---- Accounts -------------------------------------------------------------

function Accounts({ accounts, loading }: { accounts: RemoteAccount[]; loading: boolean }) {
  const [dialog, setDialog] = useState<{ kind: 'create' } | { kind: 'password'; account: RemoteAccount } | { kind: 'setup'; setup: AccountSetup } | null>(null)

  return (
    <Card>
      <div className="mb-3 flex items-center justify-between gap-4">
        <div>
          <h3 className="text-sm font-medium">Accounts</h3>
          <p className="text-xs text-muted">For signing in from other computers. On this computer you're always signed in with Windows.</p>
        </div>
        <Button onClick={() => setDialog({ kind: 'create' })}>
          <Plus className="size-4" /> Add account
        </Button>
      </div>
      {loading ? (
        <Skeleton className="h-12 rounded-xl" />
      ) : accounts.length === 0 ? (
        <p className="rounded-xl border border-dashed border-line py-6 text-center text-sm text-muted">No accounts yet.</p>
      ) : (
        <ul className="divide-y divide-line">
          {accounts.map((a) => (
            <AccountRow key={a.id} account={a} onPassword={() => setDialog({ kind: 'password', account: a })} onSetup={(setup) => setDialog({ kind: 'setup', setup })} />
          ))}
        </ul>
      )}

      {/* Closing a native <dialog> fires its close event, so each dialog only clears state it owns:
          otherwise "create" closing would also dismiss the authenticator step that replaces it. */}
      <CreateAccountDialog open={dialog?.kind === 'create'} onClose={() => setDialog((d) => (d?.kind === 'create' ? null : d))} onCreated={(setup) => setDialog({ kind: 'setup', setup })} />
      <AuthenticatorDialog setup={dialog?.kind === 'setup' ? dialog.setup : null} onClose={() => setDialog((d) => (d?.kind === 'setup' ? null : d))} />
      <PasswordDialog account={dialog?.kind === 'password' ? dialog.account : null} onClose={() => setDialog((d) => (d?.kind === 'password' ? null : d))} />
    </Card>
  )
}

function AccountRow({ account: a, onPassword, onSetup }: { account: RemoteAccount; onPassword: () => void; onSetup: (setup: AccountSetup) => void }) {
  const actions = useAccountActions()
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const locked = a.locked

  const run = async (action: () => Promise<unknown>) => {
    setError(null)
    try {
      await action()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'That didn’t work.')
    }
  }

  return (
    <li className="flex flex-wrap items-center gap-3 py-3">
      <UserRound className="size-4 text-muted" />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 text-sm">
          <span className="font-medium">{a.userName}</span>
          {!a.ready ? <Badge tone="warn">Setup not finished</Badge> : locked ? <Badge tone="danger">Locked</Badge> : <Badge tone="ok">Ready</Badge>}
        </div>
        <div className="text-xs text-muted">
          {a.lastSignInAt ? `Last signed in ${new Date(a.lastSignInAt).toLocaleString()}` : 'Never signed in'} · {a.recoveryCodesLeft} recovery code
          {a.recoveryCodesLeft === 1 ? '' : 's'} left
          {locked && ` · locked until ${new Date(a.lockedUntil!).toLocaleTimeString()} (set a new password to unlock)`}
        </div>
        {error && <div className="mt-1 text-xs text-danger">{error}</div>}
      </div>
      {confirmDelete ? (
        <>
          <span className="text-xs text-muted">Delete {a.userName}?</span>
          <Button variant="danger" className="text-xs" onClick={() => run(() => actions.remove(a.id))}>
            Delete
          </Button>
          <Button variant="ghost" className="text-xs" onClick={() => setConfirmDelete(false)}>
            Keep
          </Button>
        </>
      ) : (
        <>
          <Button variant="ghost" className="text-xs" onClick={onPassword}>
            <KeyRound className="size-3.5" /> Password
          </Button>
          <Button variant="ghost" className="text-xs" onClick={() => run(async () => onSetup(await actions.resetAuthenticator(a.id)))}>
            <Smartphone className="size-3.5" /> {a.ready ? 'New authenticator' : 'Finish setup'}
          </Button>
          <Button variant="ghost" onClick={() => setConfirmDelete(true)} aria-label={`Delete ${a.userName}`}>
            <Trash2 className="size-4" />
          </Button>
        </>
      )}
    </li>
  )
}

function CreateAccountDialog({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (setup: AccountSetup) => void }) {
  const actions = useAccountActions()
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [prevOpen, setPrevOpen] = useState(false)

  if (open !== prevOpen) {
    setPrevOpen(open)
    if (open) {
      setUserName('')
      setPassword('')
      setRepeat('')
      setError(null)
    }
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (password !== repeat) {
      setError('The passwords don’t match.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      onCreated(await actions.create(userName, password))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not create the account.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onClose={onClose} title="Add account" description="Next you'll connect an authenticator app (Microsoft Authenticator, Google Authenticator, 1Password, …).">
      <form onSubmit={submit} className="space-y-4">
        <Field label="User name" hint="Letters, digits, dots, dashes or underscores.">
          <TextInput required autoFocus value={userName} onChange={(e) => setUserName(e.target.value)} autoComplete="off" />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Password" hint="At least 12 characters.">
            <TextInput required type="password" minLength={12} value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
          </Field>
          <Field label="Repeat password">
            <TextInput required type="password" value={repeat} onChange={(e) => setRepeat(e.target.value)} autoComplete="new-password" />
          </Field>
        </div>
        {error && <p className="text-sm text-danger">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy}>
            Continue
          </Button>
        </div>
      </form>
    </Dialog>
  )
}

function AuthenticatorDialog({ setup, onClose }: { setup: AccountSetup | null; onClose: () => void }) {
  const actions = useAccountActions()
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [copied, setCopied] = useState(false)
  const [prevSetup, setPrevSetup] = useState<AccountSetup | null>(null)

  if (setup !== prevSetup) {
    setPrevSetup(setup)
    setCode('')
    setError(null)
    setCopied(false)
  }

  if (!setup) return null

  const codes = setup.setup.recoveryCodes.join('\n')
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await actions.confirm(setup.account.id, code)
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'That code didn’t work.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title={`Connect an authenticator for ${setup.account.userName}`}
      description="Scan the code with the authenticator app, save the recovery codes, then enter the code the app shows. This is shown only once."
      className="w-[min(40rem,calc(100vw-2rem))]"
    >
      <form onSubmit={submit} className="space-y-5">
        <div className="grid items-start gap-5 sm:grid-cols-[11rem_1fr]">
          <div>
            <QrCode text={setup.setup.setupUri} className="w-full rounded-xl" />
            <p className="mt-2 text-[11px] text-muted">Can't scan? Enter this key:</p>
            <p className="break-all font-mono text-[11px]">{setup.setup.secret.replace(/(.{4})/g, '$1 ').trim()}</p>
          </div>
          <div>
            <div className="mb-1.5 flex items-center justify-between">
              <span className="text-xs font-medium text-muted">Recovery codes, one use each</span>
              <span className="flex gap-1">
                <Button
                  variant="ghost"
                  className="px-2 py-1 text-xs"
                  onClick={async () => {
                    await navigator.clipboard.writeText(codes)
                    setCopied(true)
                  }}
                >
                  {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />} Copy
                </Button>
                <a
                  className="inline-flex items-center gap-1 rounded-xl px-2 py-1 text-xs text-muted transition hover:bg-accent-soft hover:text-fg"
                  href={`data:text/plain;charset=utf-8,${encodeURIComponent(`DockerBackup recovery codes for ${setup.account.userName}\n\n${codes}\n`)}`}
                  download={`dockerbackup-recovery-${setup.account.userName}.txt`}
                >
                  <Download className="size-3.5" /> Save
                </a>
              </span>
            </div>
            <ol className="grid grid-cols-2 gap-x-4 gap-y-1 rounded-xl border border-line bg-surface-strong p-3 font-mono text-sm">
              {setup.setup.recoveryCodes.map((c) => (
                <li key={c}>{c}</li>
              ))}
            </ol>
            <p className="mt-2 text-xs text-muted">Keep them somewhere safe, away from the phone. They let you sign in if the phone is lost.</p>
          </div>
        </div>
        <Field label="Code from the app">
          <TextInput
            required
            inputMode="numeric"
            autoComplete="one-time-code"
            pattern="[0-9 ]{6,7}"
            maxLength={7}
            placeholder="123 456"
            className="max-w-40 font-mono tracking-[0.3em]"
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
        </Field>
        {error && <p className="text-sm text-danger">{error}</p>}
        <div className="flex justify-end gap-2">
          <Button onClick={onClose}>Finish later</Button>
          <Button type="submit" variant="primary" disabled={busy}>
            <Check className="size-4" /> Confirm
          </Button>
        </div>
      </form>
    </Dialog>
  )
}

function PasswordDialog({ account, onClose }: { account: RemoteAccount | null; onClose: () => void }) {
  const actions = useAccountActions()
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [prevAccount, setPrevAccount] = useState<RemoteAccount | null>(null)

  if (account !== prevAccount) {
    setPrevAccount(account)
    setPassword('')
    setRepeat('')
    setError(null)
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!account) return
    if (password !== repeat) {
      setError('The passwords don’t match.')
      return
    }
    try {
      await actions.setPassword(account.id, password)
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not set the password.')
    }
  }

  return (
    <Dialog open={account !== null} onClose={onClose} title={`New password for ${account?.userName ?? ''}`} description="Signs the account out everywhere and lifts a lockout.">
      <form onSubmit={submit} className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Password" hint="At least 12 characters.">
            <TextInput required type="password" minLength={12} autoFocus value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
          </Field>
          <Field label="Repeat password">
            <TextInput required type="password" value={repeat} onChange={(e) => setRepeat(e.target.value)} autoComplete="new-password" />
          </Field>
        </div>
        {error && <p className="text-sm text-danger">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary">
            <RotateCcw className="size-4" /> Set password
          </Button>
        </div>
      </form>
    </Dialog>
  )
}

/** QR code as crisp SVG modules (dark on white, as scanners expect, in both themes). */
function QrCode({ text, className }: { text: string; className?: string }) {
  const { size, path } = useMemo(() => {
    const qr = encode(text, { ecc: 'M', border: 2 })
    let d = ''
    qr.data.forEach((row, y) =>
      row.forEach((dark, x) => {
        if (dark) d += `M${x} ${y}h1v1h-1z`
      }),
    )
    return { size: qr.size, path: d }
  }, [text])

  return (
    <svg viewBox={`0 0 ${size} ${size}`} shapeRendering="crispEdges" className={className} role="img" aria-label="QR code for the authenticator app">
      <rect width={size} height={size} fill="#fff" />
      <path d={path} fill="#000" />
    </svg>
  )
}
