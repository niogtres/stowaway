import { Cloud, Database, FolderClosed, Network, Server, Box, HardDriveUpload, type LucideIcon } from 'lucide-react'
import type { DestinationKind } from './api'

export const destinationKinds: { id: DestinationKind; label: string; icon: LucideIcon }[] = [
  { id: 'local', label: 'Local folder', icon: FolderClosed },
  { id: 'smb', label: 'Network share', icon: Network },
  { id: 's3', label: 'S3 / compatible', icon: Cloud },
  { id: 'b2', label: 'Backblaze B2', icon: HardDriveUpload },
  { id: 'azure', label: 'Azure Blob', icon: Database },
  { id: 'sftp', label: 'SFTP server', icon: Server },
  { id: 'dropbox', label: 'Dropbox', icon: Box },
]

export const kindLabel = (kind: DestinationKind) => destinationKinds.find((k) => k.id === kind)?.label ?? kind
export const kindIcon = (kind: DestinationKind) => destinationKinds.find((k) => k.id === kind)?.icon ?? FolderClosed

const linuxDumpable = new Set(['postgreSql', 'mySql', 'mariaDb', 'mongoDb', 'redis'])

/** Whether Stowaway can back up this database consistently while it runs (mirrors DatabaseDumps.Supported). */
export function canDump(database: string, platform: 'linux' | 'windows' = 'linux'): boolean {
  return platform === 'windows' ? database === 'sqlServer' : linuxDumpable.has(database)
}
