import type { ReactNode } from 'react'
import { StowawayFolderInput } from '@/components/SignInConnect'
import { Field, TextInput } from '@/components/forms'
import type { DestinationField } from '@/lib/api'
import type { Values } from '@/lib/destinations'
import { cn } from '@/lib/utils'

/**
 * Renders a destination type's fields as the server describes them. Consecutive half-width fields share a row.
 * With keepSecrets (editing), secret fields are optional: left empty, the saved value stays.
 */
export function DestinationFields({
  fields,
  values,
  onChange,
  keepSecrets = false,
}: {
  fields: DestinationField[]
  values: Values
  onChange: (key: string, value: string) => void
  keepSecrets?: boolean
}) {
  const visible = fields.filter((f) => !f.hidden)
  const rows: DestinationField[][] = []
  for (const field of visible) {
    const last = rows.at(-1)
    if (field.half && last && last.length === 1 && last[0].half) last.push(field)
    else rows.push([field])
  }

  return (
    <>
      {rows.map((row) =>
        row.length === 2 || row[0].half ? (
          <Grid key={row[0].key}>
            {row.map((f) => (
              <FieldInput key={f.key} field={f} value={values[f.key] ?? ''} onChange={(v) => onChange(f.key, v)} keepSecrets={keepSecrets} />
            ))}
          </Grid>
        ) : (
          <FieldInput key={row[0].key} field={row[0]} value={values[row[0].key] ?? ''} onChange={(v) => onChange(row[0].key, v)} keepSecrets={keepSecrets} />
        ),
      )}
    </>
  )
}

function FieldInput({ field, value, onChange, keepSecrets }: { field: DestinationField; value: string; onChange: (v: string) => void; keepSecrets: boolean }) {
  const required = field.required && !(keepSecrets && field.secret)
  const label = keepSecrets && field.secret ? `New ${field.label.charAt(0).toLowerCase()}${field.label.slice(1)}` : field.label
  const hint = keepSecrets && field.secret ? 'Leave empty to keep the current one.' : (field.hint ?? undefined)

  if (field.type === 'stowawayFolder') {
    return (
      <Field label={label} hint={hint}>
        <StowawayFolderInput value={value} onChange={onChange} />
      </Field>
    )
  }

  if (field.type === 'toggle') {
    return (
      <div className="space-y-1">
        <label className="flex items-center gap-2 text-sm font-medium">
          <input type="checkbox" checked={value === 'true'} onChange={(e) => onChange(e.target.checked ? 'true' : '')} className="size-4 accent-accent" />
          {label}
        </label>
        {hint && <p className="pl-6 text-xs text-muted">{hint}</p>}
      </div>
    )
  }

  if (field.type === 'multiline') {
    return (
      <Field label={label} hint={hint}>
        <textarea
          required={required}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          rows={4}
          spellCheck={false}
          className={cn('w-full rounded-xl border border-line bg-surface-strong px-3 py-2 text-xs outline-none focus:border-accent', field.mono && 'font-mono')}
          placeholder={field.placeholder ?? undefined}
        />
      </Field>
    )
  }

  return (
    <Field label={label} hint={hint}>
      <TextInput
        required={required}
        type={field.type === 'password' ? 'password' : undefined}
        inputMode={field.type === 'number' ? 'numeric' : undefined}
        autoComplete={field.secret ? 'new-password' : 'off'}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className={field.mono ? 'font-mono' : undefined}
        placeholder={field.placeholder ?? undefined}
      />
    </Field>
  )
}

function Grid({ children }: { children: ReactNode }) {
  return <div className="grid grid-cols-2 gap-3">{children}</div>
}
