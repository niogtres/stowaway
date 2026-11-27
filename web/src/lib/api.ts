import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

// ---- System ---------------------------------------------------------------

export interface EngineStatus {
  reachable: boolean
  endpoint: string | null
  serverVersion: string | null
  apiVersion: string | null
  osType: string | null
  operatingSystem: string | null
  hostName: string | null
  containers: number
  containersRunning: number
  containersStopped: number
  images: number
  error: string | null
  checkedAt: string
}

export interface SystemInfo {
  product: string
  version: string
  machineName: string
  operatingSystem: string
  runningAsService: boolean
  startedAt: string
  engine: EngineStatus
}

// ---- Inventory ------------------------------------------------------------

export type AppKind = 'compose' | 'standalone'
export type MountKind = 'volume' | 'bind' | 'tmpfs' | 'npipe' | 'other'
export type ProtectionStatus = 'protected' | 'stale' | 'failing' | 'unprotected' | 'excluded' | 'stateless'
export type DatabaseKind =
  | 'postgreSql'
  | 'mySql'
  | 'mariaDb'
  | 'mongoDb'
  | 'redis'
  | 'sqlServer'
  | 'elasticsearch'
  | 'influxDb'
  | 'couchDb'

export interface MountInfo {
  kind: MountKind
  source: string
  displaySource: string
  destination: string
  readOnly: boolean
  volumeName: string | null
  anonymous: boolean
  included: boolean
  exclusionReason: string | null
  sizeBytes: number | null
}

export interface ContainerInfo {
  id: string
  name: string
  image: string
  imageId: string
  imageIsLocalOnly: boolean
  state: string
  statusText: string
  health: string | null
  createdAt: string
  composeService: string | null
  database: DatabaseKind | null
  mounts: MountInfo[]
}

export interface AppInfo {
  id: string
  name: string
  kind: AppKind
  composeWorkingDir: string | null
  composeConfigFiles: string[]
  containers: ContainerInfo[]
  dataBytes: number
  sizeIncomplete: boolean
  protection: ProtectionStatus
}

export interface VolumeInfo {
  name: string
  driver: string
  createdAt: string | null
  anonymous: boolean
  orphaned: boolean
  composeProject: string | null
  usedBy: string[]
  sizeBytes: number | null
  protection: ProtectionStatus
}

export interface InventoryTotals {
  apps: number
  containers: number
  runningContainers: number
  volumes: number
  orphanedVolumes: number
  bindMounts: number
  databases: number
  localOnlyImages: number
  unprotectedItems: number
  dataBytes: number
  protectedBytes: number
  protectionScore: number | null
}

export interface InventorySnapshot {
  version: number
  capturedAt: string
  apps: AppInfo[]
  volumes: VolumeInfo[]
  totals: InventoryTotals
}

export interface InventoryResponse {
  engineReachable: boolean
  snapshot: InventorySnapshot | null
}

// ---- Transport ------------------------------------------------------------

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(method: 'GET' | 'POST', path: string): Promise<T | undefined> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  // Required by the server for state-changing calls (anti-CSRF; see RequestOriginGuard).
  if (method !== 'GET') headers['X-DockerBackup-Request'] = '1'
  const response = await fetch(path, { method, headers })
  if (!response.ok) throw new ApiError(response.status, `${response.status} ${response.statusText}`)
  if (response.status === 202 || response.status === 204) return undefined
  return (await response.json()) as T
}

const getJson = async <T>(path: string) => (await request<T>('GET', path)) as T

// ---- Hooks ----------------------------------------------------------------

export const queryKeys = {
  system: ['system'] as const,
  inventory: ['inventory'] as const,
}

export function useSystemInfo() {
  return useQuery({
    queryKey: queryKeys.system,
    queryFn: () => getJson<SystemInfo>('/api/system'),
    refetchInterval: 15_000,
  })
}

export function useInventory() {
  return useQuery({
    queryKey: queryKeys.inventory,
    queryFn: () => getJson<InventoryResponse>('/api/inventory'),
    // Live updates arrive over SignalR; this is only a safety net.
    refetchInterval: 60_000,
  })
}

export function useRefreshInventory() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => request('POST', '/api/inventory/refresh'),
    onSuccess: () => {
      // The rescan is debounced server-side; the hub announces the result. Refetch shortly as a fallback.
      setTimeout(() => queryClient.invalidateQueries({ queryKey: queryKeys.inventory }), 2500)
    },
  })
}

/** Strips the build metadata (+commit) from an informational version. */
export function displayVersion(version: string) {
  return version.split('+')[0]
}

export const databaseLabels: Record<DatabaseKind, string> = {
  postgreSql: 'PostgreSQL',
  mySql: 'MySQL',
  mariaDb: 'MariaDB',
  mongoDb: 'MongoDB',
  redis: 'Redis',
  sqlServer: 'SQL Server',
  elasticsearch: 'Elasticsearch',
  influxDb: 'InfluxDB',
  couchDb: 'CouchDB',
}
