import { Box, Cloud, Database, FolderClosed, HardDriveUpload, Network, Server, ShieldCheck, type LucideIcon } from 'lucide-react'
import type { DestinationField, DestinationKind, DestinationType } from './api'

export type Values = Record<string, string>

/** Icons the server's destination types may name (DestinationDescriptor.Icon). Unknown names fall back to a folder. */
const icons: Record<string, LucideIcon> = {
  folder: FolderClosed,
  network: Network,
  cloud: Cloud,
  hardDriveUpload: HardDriveUpload,
  database: Database,
  server: Server,
  box: Box,
  shield: ShieldCheck,
}

export const iconFor = (name: string | undefined): LucideIcon => (name && icons[name]) || FolderClosed

export const typeOf = (types: DestinationType[] | undefined, kind: DestinationKind) => types?.find((t) => t.kind === kind)

/** Splits form values into the type's settings and secrets, keeping only non-empty ones. */
export function splitValues(fields: DestinationField[], values: Values, keys?: (f: DestinationField) => boolean) {
  const settings: Values = {}
  const secrets: Values = {}
  for (const f of fields.filter((f) => !f.hidden && (keys ? keys(f) : true))) {
    const v = values[f.key]
    if (v === undefined) continue
    if (f.secret) {
      if (v) secrets[f.key] = v
    } else settings[f.key] = v
  }
  return { settings, secrets }
}
