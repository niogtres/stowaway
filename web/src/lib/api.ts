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
export type ProtectionStatus = 'protected' | 'stale' | 'failing' | 'pending' | 'unprotected' | 'excluded' | 'stateless'
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
  failingItems: number
  staleItems: number
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

// ---- Backups -------------------------------------------------------------

export type DestinationKind = 'local' | 'smb' | 's3' | 'b2' | 'azure' | 'sftp' | 'dropbox'
export type BackupStrategy = 'live' | 'stopStart'
export type RunStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled'
export type RunKind = 'backup' | 'maintenance' | 'restore'

export interface DestinationStatus {
  reachable: boolean
  error: string | null
  snapshots: number | null
  storedBytes: number | null
  lastSnapshotAt: string | null
  checkedAt: string
}

export interface Destination {
  id: string
  name: string
  kind: DestinationKind
  path: string
  smbUsername: string | null
  smbDomain: string | null
  repositoryId: string | null
  createdAt: string
  status: DestinationStatus | null
}

export interface CreateDestination {
  name: string
  kind: DestinationKind
  path: string
  smbUsername?: string
  smbPassword?: string
  smbDomain?: string
  existingRepositoryPassword?: string
  settings?: Record<string, string>
  secrets?: Record<string, string>
  dropboxSession?: string
}

export interface BackupRun {
  id: string
  appId: string
  appName: string
  destinationId: string
  strategy: BackupStrategy
  kind: RunKind
  planId: string | null
  trigger: string
  snapshotsForgotten: number | null
  status: RunStatus
  queuedAt: string
  startedAt: string | null
  finishedAt: string | null
  snapshotId: string | null
  filesProcessed: number | null
  bytesProcessed: number | null
  bytesAdded: number | null
  downtimeSeconds: number | null
  error: string | null
  log: string
}

export interface BackupProgress {
  runId: string
  phase: string
  percent: number | null
  bytesDone: number
  totalBytes: number
  filesDone: number
  totalFiles: number
}

// ---- Plans ---------------------------------------------------------------

export type PlanSelection = 'selected' | 'allApps'
export type PlanStrategy = 'auto' | 'live' | 'stopStart'

export interface RetentionPolicy {
  keepLast: number
  keepHourly: number
  keepDaily: number
  keepWeekly: number
  keepMonthly: number
  keepYearly: number
}

export interface PlanInput {
  name: string
  enabled: boolean
  selection: PlanSelection
  appIds: string[]
  excludedAppIds: string[]
  includeOrphanedVolumes: boolean
  strategy: PlanStrategy
  destinationId: string
  cron: string
  retention: RetentionPolicy
}

export interface PlanRunSummary {
  id: string
  status: RunStatus
  appName: string
  queuedAt: string
  finishedAt: string | null
  error: string | null
}

export interface Plan extends PlanInput {
  id: string
  createdAt: string
  nextRunAt: string | null
  intervalHours: number
  coveredItemIds: string[]
  recentRuns: PlanRunSummary[]
}

export interface SchedulePreview {
  valid: boolean
  error: string | null
  nextRuns: string[]
  intervalHours: number | null
}

// ---- Notifications -------------------------------------------------------

export type NotificationKind = 'email' | 'webhook' | 'ntfy'
export type SmtpSecurity = 'auto' | 'startTls' | 'sslOnConnect' | 'none'
export type WebhookFormat = 'json' | 'slack'

export interface EmailSettings {
  host: string
  port: number
  security: SmtpSecurity
  username: string | null
  password: string | null
  from: string
  to: string
}

export interface ChannelInput {
  name: string
  kind: NotificationKind
  enabled: boolean
  onFailure: boolean
  onRecovery: boolean
  onSuccess: boolean
  email?: EmailSettings | null
  webhook?: { url: string; format: WebhookFormat } | null
  ntfy?: { serverUrl: string; topic: string; accessToken: string | null } | null
}

export interface NotificationChannel {
  id: string
  name: string
  kind: NotificationKind
  enabled: boolean
  onFailure: boolean
  onRecovery: boolean
  onSuccess: boolean
  target: string
  createdAt: string
}

// ---- Snapshots & restore ------------------------------------------------

export interface SnapshotInfo {
  destinationId: string
  id: string
  shortId: string
  time: string
  host: string | null
  appId: string | null
  planId: string | null
  safety: boolean
  totalBytes: number | null
  dataAdded: number | null
}

export interface SnapshotSource {
  snapshotPath: string
  kind: 'volume' | 'bind' | 'dumps'
  source: string
  displaySource: string
  containerName: string
  destination: string
  sizeBytes: number | null
}

export interface SnapshotContents {
  appId: string
  appName: string
  appKind: string
  sources: SnapshotSource[]
}

export interface SnapshotEntry {
  name: string
  path: string
  type: 'dir' | 'file' | 'symlink' | string
  size: number | null
  modifiedAt: string | null
}

export type RestoreTarget = 'inPlace' | 'newVolume' | 'hostFolder'

export interface RestoreRequest {
  destinationId: string
  snapshotId: string
  sourcePaths: string[]
  subPath: string | null
  target: RestoreTarget
  newVolumeName: string | null
  hostFolder: string | null
  safetySnapshot: boolean
}

export interface RestorePreview {
  items: { source: string; kind: string; target: string; snapshotBytes: number | null }[]
  containersToStop: string[]
  deletesExtraFiles: boolean
  safetySnapshot: boolean
  warnings: string[]
}

// ---- Transport ------------------------------------------------------------

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

type Method = 'GET' | 'POST' | 'PUT' | 'DELETE'

async function request<T>(method: Method, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  // Required by the server for state-changing calls (anti-CSRF; see RequestOriginGuard).
  if (method !== 'GET') headers['X-DockerBackup-Request'] = '1'
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`
    try {
      const problem = (await response.json()) as { detail?: string; title?: string }
      message = problem.detail ?? problem.title ?? message
    } catch {
      // not JSON
    }
    throw new ApiError(response.status, message)
  }
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

const getJson = <T>(path: string) => request<T>('GET', path)

// ---- Hooks ----------------------------------------------------------------

export const queryKeys = {
  system: ['system'] as const,
  inventory: ['inventory'] as const,
  destinations: ['destinations'] as const,
  backups: ['backups'] as const,
  progress: ['backupProgress'] as const,
  plans: ['plans'] as const,
  channels: ['channels'] as const,
  snapshots: ['snapshots'] as const,
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
    mutationFn: () => request<void>('POST', '/api/inventory/refresh'),
    onSuccess: () => {
      // The rescan is debounced server-side; the hub announces the result. Refetch shortly as a fallback.
      setTimeout(() => queryClient.invalidateQueries({ queryKey: queryKeys.inventory }), 2500)
    },
  })
}

export function useDestinations() {
  return useQuery({
    queryKey: queryKeys.destinations,
    queryFn: () => getJson<Destination[]>('/api/destinations'),
    refetchInterval: 120_000,
  })
}

export function useCreateDestination() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: CreateDestination) => request<Destination>('POST', '/api/destinations', body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.destinations }),
  })
}

export function useCheckDestination() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => request<DestinationStatus>('POST', `/api/destinations/${id}/check`),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.destinations }),
  })
}

export function useDeleteDestination() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => request<void>('DELETE', `/api/destinations/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.destinations }),
  })
}

export interface DropboxSession {
  id: string
  state: 'pending' | 'ready' | 'failed'
  authUrl: string | null
  error: string | null
}

export const startDropbox = () => request<DropboxSession>('POST', '/api/dropbox/connect')
export const pollDropbox = (id: string) => request<DropboxSession>('GET', `/api/dropbox/connect/${id}`)

export const revealRecoveryKey = (id: string) =>
  request<{ recoveryKey: string }>('POST', `/api/destinations/${id}/recovery-key`).then((r) => r.recoveryKey)

export function useBackupRuns(limit = 200) {
  return useQuery({
    queryKey: [...queryKeys.backups, limit],
    queryFn: () => getJson<BackupRun[]>(`/api/backups?limit=${limit}`),
    refetchInterval: 60_000,
  })
}

export function useStartBackup() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { appId: string; destinationId: string; strategy: BackupStrategy }) =>
      request<BackupRun>('POST', '/api/backups', body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.backups }),
  })
}

export function useCancelBackup() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => request<void>('POST', `/api/backups/${id}/cancel`),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.backups }),
  })
}

export function usePlans() {
  return useQuery({ queryKey: queryKeys.plans, queryFn: () => getJson<Plan[]>('/api/plans'), refetchInterval: 60_000 })
}

export function useSavePlan() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, input }: { id?: string; input: PlanInput }) =>
      id ? request<Plan>('PUT', `/api/plans/${id}`, input) : request<Plan>('POST', '/api/plans', input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.plans })
      queryClient.invalidateQueries({ queryKey: queryKeys.inventory })
    },
  })
}

export function useDeletePlan() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => request<void>('DELETE', `/api/plans/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.plans }),
  })
}

export function useRunPlan() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => request<{ queued: number }>('POST', `/api/plans/${id}/run`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.backups }),
  })
}

export const previewSchedule = (cron: string) => request<SchedulePreview>('POST', '/api/plans/preview', { cron })

export function useRunMaintenance() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (destinationId: string) => request<BackupRun>('POST', `/api/destinations/${destinationId}/maintenance`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.backups }),
  })
}

export function useChannels() {
  return useQuery({ queryKey: queryKeys.channels, queryFn: () => getJson<NotificationChannel[]>('/api/notifications') })
}

export function useSaveChannel() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, input }: { id?: string; input: ChannelInput }) =>
      id ? request<NotificationChannel>('PUT', `/api/notifications/${id}`, input) : request<NotificationChannel>('POST', '/api/notifications', input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.channels }),
  })
}

export function useDeleteChannel() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => request<void>('DELETE', `/api/notifications/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.channels }),
  })
}

export const testChannel = (id: string) => request<{ ok: boolean; error: string | null }>('POST', `/api/notifications/${id}/test`)

export function useSnapshots() {
  return useQuery({ queryKey: queryKeys.snapshots, queryFn: () => getJson<SnapshotInfo[]>('/api/snapshots'), refetchInterval: 120_000 })
}

export function useSnapshotContents(destinationId: string | undefined, snapshotId: string | undefined) {
  return useQuery({
    queryKey: ['snapshotContents', destinationId, snapshotId],
    queryFn: () => getJson<SnapshotContents>(`/api/destinations/${destinationId}/snapshots/${snapshotId}/contents`),
    enabled: !!destinationId && !!snapshotId,
    staleTime: Infinity, // snapshots never change
  })
}

export function useSnapshotListing(destinationId: string, snapshotId: string, path: string | null) {
  return useQuery({
    queryKey: ['snapshotListing', destinationId, snapshotId, path],
    queryFn: () => getJson<SnapshotEntry[]>(`/api/destinations/${destinationId}/snapshots/${snapshotId}/browse?path=${encodeURIComponent(path!)}`),
    enabled: !!path,
    staleTime: Infinity,
  })
}

export const previewRestore = (body: RestoreRequest) => request<RestorePreview>('POST', '/api/restores/preview', body)

export function useStartRestore() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: RestoreRequest) => request<BackupRun>('POST', '/api/restores', body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.backups }),
  })
}

/** Latest progress per running backup, fed by SignalR (see realtime.ts). */
export function useBackupProgress() {
  return useQuery<Record<string, BackupProgress>>({
    queryKey: queryKeys.progress,
    queryFn: () => ({}),
    staleTime: Infinity,
    initialData: {},
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
