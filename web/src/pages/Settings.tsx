import { Bell, Mail, Pencil, Plus, Send, Trash2, Webhook } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Button, Dialog, Field, TextInput } from '@/components/forms'
import { Badge, Card, Skeleton } from '@/components/ui'
import {
  testChannel,
  useChannels,
  useDeleteChannel,
  useSaveChannel,
  type ChannelInput,
  type NotificationChannel,
  type NotificationKind,
  type SmtpSecurity,
  type WebhookFormat,
} from '@/lib/api'
import { cn } from '@/lib/utils'

const kindMeta: Record<NotificationKind, { icon: typeof Mail; label: string }> = {
  email: { icon: Mail, label: 'Email' },
  webhook: { icon: Webhook, label: 'Webhook' },
  ntfy: { icon: Bell, label: 'ntfy' },
}

export function Settings() {
  const channels = useChannels()
  const [editing, setEditing] = useState<NotificationChannel | undefined>()
  const [open, setOpen] = useState(false)

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <section>
        <div className="mb-3 flex items-end justify-between gap-4">
          <div>
            <h2 className="font-semibold tracking-tight">Notifications</h2>
            <p className="mt-1 text-sm text-muted">Hear about failed backups the moment they happen, and when they recover.</p>
          </div>
          <Button
            variant="primary"
            onClick={() => {
              setEditing(undefined)
              setOpen(true)
            }}
          >
            <Plus className="size-4" /> Add channel
          </Button>
        </div>

        {channels.isPending ? (
          <Skeleton className="h-24 rounded-2xl" />
        ) : (channels.data ?? []).length === 0 ? (
          <Card className="py-10 text-center text-sm text-muted">No channels yet. Email, a webhook (Slack, Discord, Teams via workflows) or ntfy push notifications.</Card>
        ) : (
          <div className="grid gap-3">
            {channels.data!.map((c) => (
              <ChannelRow
                key={c.id}
                channel={c}
                onEdit={() => {
                  setEditing(c)
                  setOpen(true)
                }}
              />
            ))}
          </div>
        )}
      </section>

      <ChannelDialog open={open} onClose={() => setOpen(false)} channel={editing} />
    </div>
  )
}

function ChannelRow({ channel: c, onEdit }: { channel: NotificationChannel; onEdit: () => void }) {
  const remove = useDeleteChannel()
  const [testing, setTesting] = useState(false)
  const [result, setResult] = useState<{ ok: boolean; error: string | null } | null>(null)
  const Icon = kindMeta[c.kind].icon
  const events = [c.onFailure && 'failures', c.onRecovery && 'recoveries', c.onSuccess && 'every success'].filter(Boolean).join(', ')

  return (
    <Card className="flex flex-wrap items-center gap-4 py-4">
      <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-accent-soft">
        <Icon className="size-5 text-accent" />
      </div>
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="font-medium">{c.name}</span>
          <Badge>{kindMeta[c.kind].label}</Badge>
          {!c.enabled && <Badge>Off</Badge>}
        </div>
        <div className="mt-0.5 truncate text-xs text-muted">
          {c.target} · {events || 'no events selected'}
        </div>
        {result && <div className={cn('mt-1 text-xs', result.ok ? 'text-ok' : 'text-danger')}>{result.ok ? 'Test sent.' : result.error}</div>}
      </div>
      <Button
        variant="secondary"
        className="text-xs"
        disabled={testing}
        onClick={async () => {
          setTesting(true)
          setResult(await testChannel(c.id).catch((e: Error) => ({ ok: false, error: e.message })))
          setTesting(false)
        }}
      >
        <Send className="size-3.5" /> {testing ? 'Sending…' : 'Send test'}
      </Button>
      <Button variant="ghost" onClick={onEdit} aria-label="Edit channel">
        <Pencil className="size-4" />
      </Button>
      <Button variant="ghost" onClick={() => remove.mutate(c.id)} aria-label="Remove channel">
        <Trash2 className="size-4" />
      </Button>
    </Card>
  )
}

const empty = {
  name: '',
  enabled: true,
  onFailure: true,
  onRecovery: true,
  onSuccess: false,
  host: '',
  port: '587',
  security: 'auto' as SmtpSecurity,
  username: '',
  password: '',
  from: '',
  to: '',
  url: '',
  format: 'json' as WebhookFormat,
  serverUrl: 'https://ntfy.sh',
  topic: '',
  token: '',
}

function ChannelDialog({ open, onClose, channel }: { open: boolean; onClose: () => void; channel?: NotificationChannel }) {
  const save = useSaveChannel()
  const [kind, setKind] = useState<NotificationKind>(channel?.kind ?? 'email')
  const [f, setF] = useState(empty)
  const [prevChannel, setPrevChannel] = useState<NotificationChannel | undefined>()
  const [prevOpen, setPrevOpen] = useState(false)

  // Reset the form whenever the dialog opens (or opens for a different channel).
  if (open !== prevOpen || channel !== prevChannel) {
    setPrevOpen(open)
    setPrevChannel(channel)
    if (open) {
      setKind(channel?.kind ?? 'email')
      setF(channel ? { ...empty, name: channel.name, enabled: channel.enabled, onFailure: channel.onFailure, onRecovery: channel.onRecovery, onSuccess: channel.onSuccess } : empty)
      save.reset()
    }
  }

  const set = (key: keyof typeof empty) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [key]: e.target.value }))
  const editingSecrets = channel ? 'Leave empty to keep the saved value' : undefined

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const input: ChannelInput = {
      name: f.name,
      kind,
      enabled: f.enabled,
      onFailure: f.onFailure,
      onRecovery: f.onRecovery,
      onSuccess: f.onSuccess,
      email: kind === 'email' ? { host: f.host, port: Number.parseInt(f.port, 10) || 587, security: f.security, username: f.username || null, password: f.password || null, from: f.from, to: f.to } : null,
      webhook: kind === 'webhook' ? { url: f.url, format: f.format } : null,
      ntfy: kind === 'ntfy' ? { serverUrl: f.serverUrl, topic: f.topic, accessToken: f.token || null } : null,
    }
    save.mutate({ id: channel?.id, input }, { onSuccess: onClose })
  }

  return (
    <Dialog open={open} onClose={onClose} title={channel ? `Edit ${channel.name}` : 'Add notification channel'}>
      <form onSubmit={submit} className="space-y-4">
        {!channel && (
          <div className="grid grid-cols-3 gap-2" role="radiogroup" aria-label="Channel type">
            {(Object.keys(kindMeta) as NotificationKind[]).map((k) => {
              const Icon = kindMeta[k].icon
              return (
                <button
                  key={k}
                  type="button"
                  role="radio"
                  aria-checked={kind === k}
                  onClick={() => setKind(k)}
                  className={cn('flex items-center justify-center gap-2 rounded-xl border p-2.5 text-sm transition', kind === k ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted')}
                >
                  <Icon className="size-4" /> {kindMeta[k].label}
                </button>
              )
            })}
          </div>
        )}

        <Field label="Name">
          <TextInput required value={f.name} onChange={set('name')} placeholder={kind === 'email' ? 'Ops mailbox' : kind === 'webhook' ? 'Team chat' : 'My phone'} />
        </Field>

        {kind === 'email' && (
          <>
            <div className="grid grid-cols-[1fr_6rem_9rem] gap-3">
              <Field label="SMTP server">
                <TextInput required value={f.host} onChange={set('host')} placeholder="smtp.example.com" />
              </Field>
              <Field label="Port">
                <TextInput required value={f.port} onChange={set('port')} inputMode="numeric" />
              </Field>
              <Field label="Security">
                <select value={f.security} onChange={set('security')} className="w-full rounded-xl border border-line bg-surface-strong px-3 py-2 text-sm">
                  <option value="auto">Automatic</option>
                  <option value="startTls">STARTTLS</option>
                  <option value="sslOnConnect">SSL/TLS</option>
                  <option value="none">None</option>
                </select>
              </Field>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Username (optional)">
                <TextInput value={f.username} onChange={set('username')} autoComplete="off" />
              </Field>
              <Field label="Password" hint={editingSecrets}>
                <TextInput type="password" value={f.password} onChange={set('password')} autoComplete="new-password" />
              </Field>
              <Field label="From">
                <TextInput required value={f.from} onChange={set('from')} placeholder="dockerbackup@example.com" />
              </Field>
              <Field label="To" hint="Separate several with commas">
                <TextInput required value={f.to} onChange={set('to')} placeholder="you@example.com" />
              </Field>
            </div>
          </>
        )}

        {kind === 'webhook' && (
          <>
            <Field label="URL">
              <TextInput required value={f.url} onChange={set('url')} placeholder="https://hooks.slack.com/services/…" className="font-mono" />
            </Field>
            <Field label="Format" hint="Slack-compatible also works for Discord (append /slack to the webhook URL) and Mattermost.">
              <select value={f.format} onChange={set('format')} className="w-full rounded-xl border border-line bg-surface-strong px-3 py-2 text-sm">
                <option value="json">Generic JSON</option>
                <option value="slack">Slack-compatible</option>
              </select>
            </Field>
          </>
        )}

        {kind === 'ntfy' && (
          <div className="grid grid-cols-2 gap-3">
            <Field label="Server">
              <TextInput required value={f.serverUrl} onChange={set('serverUrl')} />
            </Field>
            <Field label="Topic" hint="Pick something hard to guess on ntfy.sh">
              <TextInput required value={f.topic} onChange={set('topic')} className="font-mono" />
            </Field>
            <Field label="Access token (optional)" hint={editingSecrets}>
              <TextInput type="password" value={f.token} onChange={set('token')} autoComplete="new-password" />
            </Field>
          </div>
        )}

        <fieldset className="flex flex-wrap gap-x-5 gap-y-2 text-sm">
          <legend className="mb-2 text-xs font-medium text-muted">Send when</legend>
          {(
            [
              ['onFailure', 'A backup fails'],
              ['onRecovery', 'It works again'],
              ['onSuccess', 'Every success'],
              ['enabled', 'Channel enabled'],
            ] as const
          ).map(([key, label]) => (
            <label key={key} className="flex items-center gap-2">
              <input type="checkbox" className="accent-[var(--accent)]" checked={f[key]} onChange={(e) => setF((x) => ({ ...x, [key]: e.target.checked }))} />
              {label}
            </label>
          ))}
        </fieldset>

        {save.error && <p className="text-sm text-danger">{save.error.message}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={save.isPending}>
            {channel ? 'Save' : 'Add channel'}
          </Button>
        </div>
      </form>
    </Dialog>
  )
}
