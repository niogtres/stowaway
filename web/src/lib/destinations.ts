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

/** Database kinds Stowaway can dump while they run (mirrors DatabaseDumps.Supported). */
export const dumpableDatabases = new Set(['postgreSql', 'mySql', 'mariaDb', 'mongoDb', 'redis'])
