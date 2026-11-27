import { ShieldCheck } from 'lucide-react'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { confirmFreshCode, setFreshCodePrompt } from '@/lib/api'
import { Button, Dialog, Field, TextInput } from './forms'

/**
 * Asks a remote user for a fresh authenticator code when a protected change needs one (restores, deleting,
 * changing plans, destinations or notifications). Once confirmed, changes are allowed for 10 minutes.
 */
export function FreshCodePrompt() {
  const [open, setOpen] = useState(false)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const pending = useRef<((ok: boolean) => void) | null>(null)

  useEffect(() => {
    setFreshCodePrompt(
      () =>
        new Promise<boolean>((resolve) => {
          pending.current?.(false)
          pending.current = resolve
          setCode('')
          setError(null)
          setOpen(true)
        }),
    )
    return () => setFreshCodePrompt(null)
  }, [])

  const finish = (ok: boolean) => {
    pending.current?.(ok)
    pending.current = null
    setOpen(false)
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await confirmFreshCode(code)
      finish(true)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'That code didn’t work.')
      setCode('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open={open}
      onClose={() => finish(false)}
      title="Confirm with your authenticator"
      description="This change is protected when you're signed in remotely. Enter the code your authenticator app shows now; you won't be asked again for 10 minutes."
    >
      <form onSubmit={submit} className="space-y-4">
        <Field label="Code from your authenticator app">
          <TextInput
            required
            autoFocus
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
          <Button onClick={() => finish(false)}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy}>
            <ShieldCheck className="size-4" /> Confirm
          </Button>
        </div>
      </form>
    </Dialog>
  )
}
