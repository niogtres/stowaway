import { Check, Copy, Download, Eye, KeyRound, ShieldCheck, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { Button, Dialog, TextInput } from '@/components/forms'
import { Badge } from '@/components/ui'
import { checkRecoveryKey, confirmRecoveryKey, revealRecoveryKey, useAuthMode, type Destination, type RecoveryKeyReveal } from '@/lib/api'
import { cn } from '@/lib/utils'

/** The amber "key not saved" badge on a destination; opens the key dialog. */
export function KeyNotSavedBadge({ onClick }: { onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} title="Without the recovery key, these backups can't be restored if this computer is lost. Save it now.">
      <Badge tone="warn">
        <KeyRound className="size-3" />
        Recovery key not saved
      </Badge>
    </button>
  )
}

/**
 * The recovery key of one destination: reveal, copy, download the recovery kit, confirm it's stored away from this
 * computer, and check a stored key. Right after adding a destination (justAdded) it's the last step of the setup.
 */
export function RecoveryKeyDialog({ destination, open, onClose, justAdded = false }: { destination: Destination; open: boolean; onClose: () => void; justAdded?: boolean }) {
  const remote = useAuthMode(open).data?.remote ?? false
  const [revealed, setRevealed] = useState<RecoveryKeyReveal | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const [stored, setStored] = useState(false)
  const [confirmedNow, setConfirmedNow] = useState(false)
  const [saving, setSaving] = useState(false)
  const confirmed = !!destination.recoveryKeyConfirmedAt || confirmedNow

  const close = () => {
    setRevealed(null)
    setError(null)
    setCopied(false)
    setStored(false)
    onClose()
  }

  const done = async () => {
    if (stored && !confirmed) {
      setSaving(true)
      try {
        await confirmRecoveryKey(destination.id)
      } catch (e) {
        setError((e as Error).message)
        setSaving(false)
        return
      }
      setSaving(false)
    }
    close()
  }

  const footer = remote ? (
    <Button onClick={close}>Close</Button>
  ) : confirmed ? (
    <Button onClick={close}>Done</Button>
  ) : (
    <>
      <Button variant="ghost" onClick={close} title="The destination keeps showing that its key isn't saved.">
        Remind me later
      </Button>
      <Button variant="primary" onClick={done} disabled={!stored || saving}>
        Done
      </Button>
    </>
  )

  return (
    <Dialog
      open={open}
      onClose={close}
      title={justAdded ? `Save the recovery key for ${destination.name}` : 'Recovery key'}
      description="Every backup in this destination is encrypted with this key. Without it, nobody can restore them: not you on a new computer, not anyone else."
      footer={footer}
      className="w-[min(40rem,calc(100vw-2rem))]"
    >
      {remote ? (
        <div className="flex items-start gap-2 rounded-xl border border-warn/40 bg-warn/10 p-3 text-sm">
          <TriangleAlert className="mt-0.5 size-4 shrink-0 text-warn" />
          <span>
            Recovery keys are only shown on the computer that runs Stowaway, never through remote access. Open Stowaway there to save this key
            {confirmed ? '' : ': until then, the destination shows that its key isn’t saved'}.
          </span>
        </div>
      ) : (
        <div className="space-y-4">
          <div className={cn('rounded-xl border p-3 text-sm', confirmed ? 'border-ok/40 bg-ok/10' : 'border-warn/40 bg-warn/10')}>
            {confirmed ? (
              <span className="flex items-center gap-2">
                <ShieldCheck className="size-4 text-ok" /> You've confirmed this key is stored away from this computer.
              </span>
            ) : (
              <>
                <strong>If this computer is lost, the key goes with it.</strong> Store it somewhere else: a password manager, a printed copy, or a USB
                stick in a drawer. The recovery kit is a text file with the key and the steps to restore on another computer.
              </>
            )}
          </div>

          {revealed ? (
            <div className="space-y-2">
              <div className="flex items-center gap-2">
                <code className="min-w-0 flex-1 rounded-xl border border-line bg-surface px-3 py-2 font-mono text-sm break-all">{revealed.recoveryKey}</code>
                <Button
                  onClick={async () => {
                    await navigator.clipboard.writeText(revealed.recoveryKey)
                    setCopied(true)
                  }}
                  aria-label="Copy recovery key"
                >
                  {copied ? <Check className="size-4" /> : <Copy className="size-4" />} {copied ? 'Copied' : 'Copy'}
                </Button>
              </div>
              <a
                className="inline-flex items-center gap-1.5 rounded-xl border border-line px-3 py-2 text-sm transition hover:border-accent"
                href={`data:text/plain;charset=utf-8,${encodeURIComponent(revealed.kit)}`}
                download={revealed.kitFileName}
              >
                <Download className="size-4" /> Download recovery kit
              </a>
            </div>
          ) : (
            <Button
              variant="primary"
              onClick={() =>
                revealRecoveryKey(destination.id)
                  .then(setRevealed)
                  .catch((e: Error) => setError(e.message))
              }
            >
              <Eye className="size-4" /> Show key
            </Button>
          )}

          {!confirmed && (
            <label className="flex items-start gap-2 text-sm">
              <input type="checkbox" checked={stored} onChange={(e) => setStored(e.target.checked)} className="mt-0.5 size-4 accent-accent" />
              <span>I've stored this key (or the recovery kit) somewhere other than this computer.</span>
            </label>
          )}

          <KeyCheck destination={destination} onMatch={() => setConfirmedNow(true)} />
          {destination.recoveryKeyFingerprint && (
            <p className="text-xs text-muted">
              Key check value <span className="font-mono">{destination.recoveryKeyFingerprint}</span>: the same value is printed in this destination's recovery kit, so you can tell
              kits apart without revealing a key.
            </p>
          )}
          {error && <p className="text-sm text-danger">{error}</p>}
        </div>
      )}
    </Dialog>
  )
}

/** Paste a stored key and learn whether it's this destination's key (a match also confirms it's saved). */
function KeyCheck({ destination, onMatch }: { destination: Destination; onMatch: () => void }) {
  const [open, setOpen] = useState(false)
  const [candidate, setCandidate] = useState('')
  const [result, setResult] = useState<'match' | 'mismatch' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [checking, setChecking] = useState(false)

  if (!open)
    return (
      <button type="button" onClick={() => setOpen(true)} className="text-sm text-accent hover:underline">
        Check a key you stored
      </button>
    )

  const check = async () => {
    setChecking(true)
    setError(null)
    try {
      const matches = await checkRecoveryKey(destination.id, candidate)
      setResult(matches ? 'match' : 'mismatch')
      if (matches) onMatch()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setChecking(false)
    }
  }

  return (
    <div className="space-y-2 rounded-xl border border-line p-3">
      <p className="text-sm">Paste the key from where you stored it, to make sure it's the right one and complete.</p>
      <div className="flex gap-2">
        <TextInput
          type="password"
          value={candidate}
          onChange={(e) => {
            setCandidate(e.target.value)
            setResult(null)
          }}
          className="font-mono"
          aria-label="Stored recovery key"
          autoComplete="off"
        />
        <Button onClick={check} disabled={checking || !candidate.trim()}>
          {checking ? 'Checking…' : 'Check'}
        </Button>
      </div>
      {result === 'match' && (
        <p className="flex items-center gap-1.5 text-sm text-ok">
          <Check className="size-4" /> That's the key for {destination.name}.
        </p>
      )}
      {result === 'mismatch' && <p className="text-sm text-danger">That isn't the key for {destination.name}. It may belong to another destination, or be incomplete.</p>}
      {error && <p className="text-sm text-danger">{error}</p>}
    </div>
  )
}
