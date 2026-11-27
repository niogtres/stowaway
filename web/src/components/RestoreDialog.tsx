import { Link } from '@tanstack/react-router'
import { FolderOpen, Layers, RotateCcw, ShieldCheck, TriangleAlert } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Button, Dialog, Field, TextInput } from '@/components/forms'
import { previewRestore, useStartRestore, type RestorePreview, type RestoreRequest, type RestoreTarget, type SnapshotInfo, type SnapshotSource } from '@/lib/api'
import { cn, formatBytes } from '@/lib/utils'

export interface RestoreScope {
  sources: SnapshotSource[]
  /** File or folder inside the single source, relative to it (e.g. "/config/app.db"). */
  subPath: string | null
  label: string
}

const stamp = (iso: string) => {
  const d = new Date(iso)
  const p = (n: number) => n.toString().padStart(2, '0')
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}`
}

/** Mounted only while open (see callers), so every opening starts with fresh state. */
export function RestoreDialog({ snapshot, appName, scope, onClose }: { snapshot: SnapshotInfo; appName: string; scope: RestoreScope; onClose: () => void }) {
  const start = useStartRestore()
  const single = scope.sources.length === 1 ? scope.sources[0] : null
  const dumpsOnly = scope.sources.every((s) => s.kind === 'dumps')
  const [target, setTarget] = useState<RestoreTarget>(scope.subPath || dumpsOnly ? 'hostFolder' : 'inPlace')
  const [volumeName, setVolumeName] = useState(`${single?.kind === 'volume' ? single.source : appName.replace(/[^a-zA-Z0-9_.-]/g, '-')}-restored-${stamp(snapshot.time)}`)
  const [hostFolder, setHostFolder] = useState(`C:\\DockerBackup-restores\\${appName.replace(/[^a-zA-Z0-9_.-]/g, '-')}-${stamp(snapshot.time)}`)
  const [safety, setSafety] = useState(true)
  const [confirmText, setConfirmText] = useState('')
  const [preview, setPreview] = useState<RestorePreview | null>(null)
  const [previewError, setPreviewError] = useState<string | null>(null)

  const request: RestoreRequest = {
    destinationId: snapshot.destinationId,
    snapshotId: snapshot.id,
    sourcePaths: scope.sources.map((s) => s.snapshotPath),
    subPath: scope.subPath,
    target,
    newVolumeName: target === 'newVolume' ? volumeName : null,
    hostFolder: target === 'hostFolder' ? hostFolder : null,
    safetySnapshot: target === 'inPlace' && safety,
  }
  const requestKey = JSON.stringify(request)

  useEffect(() => {
    const handle = setTimeout(() => {
      previewRestore(JSON.parse(requestKey) as RestoreRequest)
        .then((p) => {
          setPreview(p)
          setPreviewError(null)
        })
        .catch((e: Error) => {
          setPreview(null)
          setPreviewError(e.message)
        })
    }, 300)
    return () => clearTimeout(handle)
  }, [requestKey])

  const inPlace = target === 'inPlace'
  const confirmed = !inPlace || confirmText.trim() === appName
  const blocked = !!previewError || !!preview?.warnings.some((w) => w.includes('already exists'))

  return (
    <Dialog
      open
      onClose={onClose}
      title={`Restore ${scope.label}`}
      description={`From the snapshot of ${new Date(snapshot.time).toLocaleString()}.`}
      className="w-[min(40rem,calc(100vw-2rem))]"
    >
      {start.isSuccess ? (
        <div className="space-y-4 text-sm">
          <p>The restore has started. You can follow it on the Activity page.</p>
          <div className="flex justify-end gap-2">
            <Button onClick={onClose}>Close</Button>
            <Link to="/activity" className="inline-flex items-center gap-2 rounded-xl bg-accent px-3.5 py-2 text-sm font-medium text-bg">
              Open Activity
            </Link>
          </div>
        </div>
      ) : (
        <div className="space-y-5">
          <div className="grid grid-cols-3 gap-2" role="radiogroup" aria-label="Restore to">
            {(
              [
                { id: 'inPlace', icon: RotateCcw, title: 'Where it was', text: 'Overwrite the current data' },
                { id: 'newVolume', icon: Layers, title: 'New volume', text: 'Leave the original alone' },
                { id: 'hostFolder', icon: FolderOpen, title: 'Folder on this PC', text: 'Pick files out by hand' },
              ] as { id: RestoreTarget; icon: typeof Layers; title: string; text: string }[]
            ).map((o) => (
              <button
                key={o.id}
                type="button"
                role="radio"
                aria-checked={target === o.id}
                disabled={dumpsOnly && o.id === 'inPlace'}
                title={dumpsOnly && o.id === 'inPlace' ? 'Load dumps into the database by hand after restoring them to a folder or volume' : undefined}
                onClick={() => setTarget(o.id)}
                className={cn('rounded-xl border p-3 text-left transition disabled:opacity-40', target === o.id ? 'border-accent bg-accent-soft' : 'border-line hover:border-muted')}
              >
                <span className="flex items-center gap-2 text-sm font-medium">
                  <o.icon className={cn('size-4', target === o.id ? 'text-accent' : 'text-muted')} /> {o.title}
                </span>
                <span className="mt-1 block text-xs text-muted">{o.text}</span>
              </button>
            ))}
          </div>

          {target === 'newVolume' && (
            <Field label={scope.sources.length > 1 ? 'Volume name prefix (a number is added per source)' : 'New volume name'}>
              <TextInput value={volumeName} onChange={(e) => setVolumeName(e.target.value)} className="font-mono" />
            </Field>
          )}
          {target === 'hostFolder' && (
            <Field label="Folder" hint="Created if it doesn't exist.">
              <TextInput value={hostFolder} onChange={(e) => setHostFolder(e.target.value)} className="font-mono" />
            </Field>
          )}
          {inPlace && (
            <label className="flex items-start gap-2 text-sm">
              <input type="checkbox" className="mt-0.5 accent-[var(--accent)]" checked={safety} onChange={(e) => setSafety(e.target.checked)} />
              <span>
                Take a safety snapshot first
                <span className="block text-xs text-muted">Saves the current data so this restore can be undone.</span>
              </span>
            </label>
          )}

          <div className="rounded-xl border border-line p-4 text-sm" aria-live="polite">
            <div className="mb-2 text-xs font-medium text-muted">What will happen</div>
            {previewError ? (
              <p className="text-danger">{previewError}</p>
            ) : !preview ? (
              <p className="text-muted">Working it out…</p>
            ) : (
              <ul className="space-y-1.5">
                {preview.safetySnapshot && (
                  <li className="flex gap-2">
                    <ShieldCheck className="mt-0.5 size-4 shrink-0 text-ok" /> The current data is saved as a safety snapshot first.
                  </li>
                )}
                {preview.containersToStop.length > 0 && (
                  <li className="flex gap-2">
                    <TriangleAlert className="mt-0.5 size-4 shrink-0 text-warn" />
                    <span>
                      Stops <strong>{preview.containersToStop.join(', ')}</strong> during the restore and starts them again afterwards.
                    </span>
                  </li>
                )}
                {preview.items.map((i) => (
                  <li key={i.source + i.target} className="flex gap-2">
                    <RotateCcw className="mt-0.5 size-4 shrink-0 text-accent" />
                    <span className="min-w-0 break-words">
                      {scope.subPath ? <code className="font-mono text-xs">{scope.subPath}</code> : <>{i.source}</>} {i.target}
                      {i.snapshotBytes !== null && !scope.subPath && <span className="text-muted"> ({formatBytes(i.snapshotBytes)})</span>}
                    </span>
                  </li>
                ))}
                {preview.deletesExtraFiles && (
                  <li className="flex gap-2">
                    <TriangleAlert className="mt-0.5 size-4 shrink-0 text-warn" /> Files that didn't exist at the time of the snapshot are removed.
                  </li>
                )}
                {preview.warnings.map((w) => (
                  <li key={w} className="flex gap-2 text-warn">
                    <TriangleAlert className="mt-0.5 size-4 shrink-0" /> {w}
                  </li>
                ))}
              </ul>
            )}
          </div>

          {inPlace && (
            <Field label={`Type ${appName} to confirm overwriting the current data`}>
              <TextInput value={confirmText} onChange={(e) => setConfirmText(e.target.value)} autoComplete="off" />
            </Field>
          )}

          {start.error && <p className="text-sm text-danger">{start.error.message}</p>}
          <div className="flex justify-end gap-2">
            <Button onClick={onClose}>Cancel</Button>
            <Button variant={inPlace ? 'danger' : 'primary'} disabled={!confirmed || blocked || !preview || start.isPending} onClick={() => start.mutate(request)}>
              <RotateCcw className="size-4" /> {inPlace ? 'Overwrite and restore' : 'Restore'}
            </Button>
          </div>
        </div>
      )}
    </Dialog>
  )
}
