import { Check, ExternalLink } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { Button, TextInput } from '@/components/forms'
import { completeDropbox, pollDropbox, startDropbox } from '@/lib/api'
import { cn } from '@/lib/utils'

export interface DropboxSignIn {
  id: string
  state: 'pending' | 'ready' | 'failed'
  error: string | null
}

/**
 * Dropbox sign-in. The browser normally comes back to this computer by itself. On another computer (remote access)
 * it ends on a localhost page that can't load; pasting that address here finishes the sign-in.
 */
export function DropboxConnect({ value, onChange, label = 'Connect Dropbox' }: { value: DropboxSignIn | null; onChange: (s: DropboxSignIn | null) => void; label?: string }) {
  const [starting, setStarting] = useState(false)
  const [pasted, setPasted] = useState('')
  const [pasteError, setPasteError] = useState<string | null>(null)
  const [sending, setSending] = useState(false)
  // Only chooses which instructions to show; nothing about access depends on it.
  const remote = !['localhost', '127.0.0.1', '[::1]'].includes(window.location.hostname)

  // Poll the sign-in until the browser flow completes.
  useEffect(() => {
    if (!value || value.state !== 'pending') return
    const timer = setInterval(() => {
      pollDropbox(value.id)
        .then((s) => onChange({ id: s.id, state: s.state, error: s.error }))
        .catch(() => onChange({ ...value, state: 'failed', error: 'The sign-in session expired.' }))
    }, 1500)
    return () => clearInterval(timer)
  }, [value, onChange])

  const connect = async () => {
    setStarting(true)
    setPasted('')
    setPasteError(null)
    try {
      const session = await startDropbox()
      onChange({ id: session.id, state: session.state, error: session.error })
      if (session.authUrl) window.open(session.authUrl, '_blank', 'noopener')
    } catch (e) {
      onChange({ id: '', state: 'failed', error: (e as Error).message })
    } finally {
      setStarting(false)
    }
  }

  const finish = async () => {
    if (!value) return
    setSending(true)
    setPasteError(null)
    try {
      const s = await completeDropbox(value.id, pasted)
      onChange({ id: s.id, state: s.state, error: s.error })
    } catch (e) {
      setPasteError((e as Error).message)
    } finally {
      setSending(false)
    }
  }

  const pasteBox = (
    <div className="space-y-2">
      <div className="flex gap-2">
        <TextInput
          value={pasted}
          onChange={(e) => setPasted(e.target.value)}
          className="font-mono"
          placeholder="http://localhost:53682/?state=…&code=…"
          aria-label="Address from the page that couldn't load"
        />
        <Button variant={remote ? 'primary' : 'secondary'} onClick={finish} disabled={sending || !pasted.trim()}>
          {sending ? 'Finishing…' : 'Finish'}
        </Button>
      </div>
      {pasteError && <p className="text-danger">{pasteError}</p>}
    </div>
  )

  return (
    <div className="rounded-xl border border-line p-4">
      {value?.state === 'ready' ? (
        <p className="flex items-center gap-2 text-sm text-ok">
          <Check className="size-4" /> Dropbox connected.
        </p>
      ) : remote ? (
        // The browser isn't on the Stowaway computer, so Dropbox's return trip can't reach it: walk through the extra step.
        <div className="space-y-3 text-sm">
          <p className="font-medium">You're on another computer, so connecting takes one extra step:</p>
          <ol className="space-y-2">
            <Step n={1} active={value?.state !== 'pending'}>
              Choose <strong>{label}</strong>. Dropbox opens in a new tab: sign in and choose <strong>Allow</strong>.
            </Step>
            <Step n={2} active={value?.state === 'pending'}>
              The tab then shows an error like <em>“localhost refused to connect”</em>. That's expected. Copy the whole address from that tab's
              address bar. It starts with <code className="font-mono">http://localhost:53682/</code>.
            </Step>
            <Step n={3} active={value?.state === 'pending'}>
              Come back to this tab, paste the address below and choose <strong>Finish</strong>. The address works only once and for a few minutes.
            </Step>
          </ol>
          {value?.state === 'pending' ? (
            <>
              {pasteBox}
              <button type="button" onClick={connect} className="text-xs text-accent hover:underline">
                Start again
              </button>
            </>
          ) : (
            <Button variant="primary" onClick={connect} disabled={starting}>
              <ExternalLink className="size-4" /> {starting ? 'Starting…' : label}
            </Button>
          )}
        </div>
      ) : value?.state === 'pending' ? (
        <div className="space-y-3 text-sm">
          <p>
            Finish signing in on the Dropbox page that just opened, and choose <strong>Allow</strong>. This window updates by itself.{' '}
            <button type="button" onClick={connect} className="text-accent hover:underline">
              Start again
            </button>
          </p>
          <div className="space-y-2 border-t border-line pt-3">
            <p className="text-muted">
              Did the Dropbox tab end on a page that can't load (an address starting with <code className="font-mono">http://localhost:53682/</code>)? Copy
              that whole address and paste it here.
            </p>
            {pasteBox}
          </div>
        </div>
      ) : (
        <div className="flex items-center justify-between gap-3">
          <p className="text-sm text-muted">Sign in to Dropbox and allow access. Stowaway only keeps the access it is given.</p>
          <Button variant="primary" onClick={connect} disabled={starting}>
            <ExternalLink className="size-4" /> {starting ? 'Starting…' : label}
          </Button>
        </div>
      )}
      {value?.state === 'failed' && <p className="mt-2 text-sm text-danger">{value.error}</p>}
    </div>
  )
}

function Step({ n, active, children }: { n: number; active: boolean; children: ReactNode }) {
  return (
    <li className={cn('flex gap-3', !active && 'text-muted')}>
      <span className={cn('grid size-6 shrink-0 place-items-center rounded-full text-xs font-semibold', active ? 'bg-accent text-bg' : 'bg-accent-soft text-muted')}>
        {n}
      </span>
      <span className="pt-0.5">{children}</span>
    </li>
  )
}

/** The Dropbox folder field: everything goes under /Stowaway/, the user picks an optional subfolder. */
export function DropboxFolderInput({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  return (
    <div className="flex items-center rounded-xl border border-line bg-surface-strong focus-within:border-accent">
      <span className="pl-3 font-mono text-sm text-muted">/Stowaway/</span>
      <input
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="min-w-0 flex-1 bg-transparent py-2 pr-3 font-mono text-sm outline-none"
        placeholder="(optional subfolder)"
        aria-label="Subfolder in /Stowaway"
      />
    </div>
  )
}
