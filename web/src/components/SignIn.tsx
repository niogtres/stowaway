import { KeyRound, LoaderCircle } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import logoMark from '@/assets/logo-mark.svg'
import { signIn } from '@/lib/api'
import { Button, Field, TextInput } from './forms'

/** Shown to other computers (remote access): user name, password and the code from the authenticator app. */
export function SignIn() {
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [recovery, setRecovery] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await signIn(userName, password, code)
      // A fresh start picks up the session everywhere, including the live-updates connection.
      window.location.reload()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Sign-in failed.')
      setCode('')
      setBusy(false)
    }
  }

  return (
    <div className="relative grid h-full place-items-center px-6">
      <div className="aurora pointer-events-none fixed inset-0 -z-10" aria-hidden />
      <form onSubmit={submit} className="glass w-full max-w-sm rounded-2xl p-8">
        <img src={logoMark} alt="" className="mx-auto mb-4 size-12" />
        <h1 className="text-center text-lg font-semibold tracking-tight">Sign in to DockerBackup</h1>
        <p className="mb-6 mt-1 text-center text-sm text-muted">Remote access to {window.location.hostname}</p>

        <div className="space-y-4">
          <Field label="User name">
            <TextInput required autoFocus autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} />
          </Field>
          <Field label="Password">
            <TextInput required type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          {recovery ? (
            <Field label="Recovery code" hint="Each recovery code works once.">
              <TextInput required autoComplete="off" placeholder="xxxxx-xxxxx" className="font-mono" value={code} onChange={(e) => setCode(e.target.value)} />
            </Field>
          ) : (
            <Field label="Code from your authenticator app">
              <TextInput
                required
                inputMode="numeric"
                autoComplete="one-time-code"
                pattern="[0-9 ]{6,7}"
                maxLength={7}
                placeholder="123 456"
                className="font-mono tracking-[0.3em]"
                value={code}
                onChange={(e) => setCode(e.target.value)}
              />
            </Field>
          )}
        </div>

        {error && (
          <p role="alert" className="mt-4 text-sm text-danger">
            {error}
          </p>
        )}

        <Button type="submit" variant="primary" className="mt-6 w-full" disabled={busy}>
          {busy ? <LoaderCircle className="size-4 animate-spin" /> : <KeyRound className="size-4" />}
          Sign in
        </Button>
        <button
          type="button"
          className="mt-3 w-full text-center text-xs text-muted transition hover:text-fg"
          onClick={() => {
            setRecovery((r) => !r)
            setCode('')
          }}
        >
          {recovery ? 'Use the authenticator app instead' : 'Lost your phone? Use a recovery code'}
        </button>
      </form>
    </div>
  )
}
