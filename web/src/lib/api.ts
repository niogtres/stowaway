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
  /** A host folder left out by default that the owner may include. */
  canInclude: boolean
  includedByOwner: boolean
  /** Why a folder outside the app is included by default (e.g. a Business Central container's own folder). */
  note: string | null
  /** Left out because it needs no backup at all (e.g. Business Central's artifact cache), not because it lives elsewhere. */
  notNeeded: boolean
}

/** The product a container runs, when Stowaway recognizes it. */
export interface AppProduct {
  /** The server-side app profile that recognized it, e.g. business-central. */
  profile: string
  name: string
  version: string | null
  country: string | null
  /** What Stowaway does differently for this product, for the app's card. */
  note: string | null
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
  product: AppProduct | null
  /** Whether Stowaway can back up this database consistently while it runs (decided by the server's database providers). */
  dumpSupported: boolean
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
  /** Databases inside the containers, sized by the last backup. */
  databaseBytes: number | null
  sizeIncomplete: boolean
  protection: ProtectionStatus
  platform: 'linux' | 'windows'
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
  exclusionReason: string | null
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
  dataItems: number
  protectedItems: number
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

export type DestinationKind = 'local' | 'smb' | 's3' | 'b2' | 'azure' | 'sftp' | 'dropbox' | 'rest' | 'gcs' | 'oneDrive'
export type BackupStrategy = 'live' | 'stopStart'
export type RunStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled'
export type RunKind = 'backup' | 'maintenance' | 'restore' | 'drill'

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
  /** The destination's non-secret field values (folder, bucket, host…), keyed like its type's fields. */
  settings: Record<string, string>
}

/** A field a destination type needs, as the server describes it (GET /api/destination-types). */
export interface DestinationField {
  key: string
  label: string
  type: 'text' | 'password' | 'multiline' | 'number' | 'stowawayFolder' | 'toggle'
  section: 'location' | 'credentials'
  required: boolean
  secret: boolean
  hidden: boolean
  placeholder: string | null
  hint: string | null
  default: string | null
  mono: boolean
  half: boolean
}

export interface DestinationType {
  kind: DestinationKind
  label: string
  icon: string
  namePlaceholder: string
  fields: DestinationField[]
  /** A browser sign-in instead of typed credentials: the service ('dropbox', 'onedrive'). */
  signIn: string | null
  notes: string | null
}

/** settings: the type's non-secret fields; secrets: its secret ones. */
export interface CreateDestination {
  name: string
  kind: DestinationKind
  settings: Record<string, string>
  secrets: Record<string, string>
  existingRepositoryPassword?: string
  signInSession?: string
  /** Reuse the credentials (e.g. the Dropbox or OneDrive sign-in) of this destination of the same type. */
  credentialsFrom?: string
}

/** Only the fields being changed. A new location or credentials must open the same backups. */
export interface UpdateDestination {
  name?: string
  settings?: Record<string, string>
  secrets?: Record<string, string>
  signInSession?: string
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
  verifyRestores: boolean
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
  kind: 'volume' | 'bind' | 'dumps' | 'definition'
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
  recreateContainers?: boolean
}

export interface RestorePreview {
  items: { source: string; kind: string; target: string; snapshotBytes: number | null }[]
  containersToStop: string[]
  deletesExtraFiles: boolean
  safetySnapshot: boolean
  warnings: string[]
  containersToRecreate: string[] | null
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

/**
 * Remote sessions must confirm protected changes with a fresh authenticator code (HTTP 428). The app registers a
 * prompt here; it resolves true once the code was accepted, and the request is then sent again.
 */
let freshCodePrompt: (() => Promise<boolean>) | null = null
export function setFreshCodePrompt(prompt: (() => Promise<boolean>) | null) {
  freshCodePrompt = prompt
}

async function request<T>(method: Method, path: string, body?: unknown, retried = false): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  // Required by the server for state-changing calls (anti-CSRF; see RequestOriginGuard).
  if (method !== 'GET') headers['X-Stowaway-Request'] = '1'
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (response.status === 428 && !retried && freshCodePrompt && (await freshCodePrompt())) {
    return request<T>(method, path, body, true)
  }
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

export interface Me {
  name: string
  signInRequired: boolean
  method: 'windows' | 'account' | 'none'
  remote: boolean
}

export function useMe() {
  return useQuery({ queryKey: ['me'], queryFn: () => getJson<Me>('/api/me'), staleTime: Infinity, retry: false })
}

// ---- Sign-in and remote access ----------------------------------------------

export function useAuthMode(enabled: boolean) {
  return useQuery({
    queryKey: ['authMode'],
    queryFn: () => getJson<{ remote: boolean; signIn: 'windows' | 'account' | 'none' }>('/api/auth/mode'),
    enabled,
    staleTime: Infinity,
    retry: false,
  })
}

export const signIn = (userName: string, password: string, code: string) => request<void>('POST', '/api/auth/login', { userName, password, code })
export const signOut = () => request<void>('POST', '/api/auth/logout')
export const confirmFreshCode = (code: string) => request<void>('POST', '/api/auth/elevate', { code })

export interface AuditEntry {
  id: number
  at: string
  actor: string
  address: string
  remote: boolean
  action: string
  detail: string
  status: number | null
}

export function useAudit() {
  return useQuery({ queryKey: ['audit'], queryFn: () => getJson<AuditEntry[]>('/api/audit?limit=200'), refetchInterval: 30_000 })
}

export type FirewallProfile = 'domain' | 'private' | 'public'
export type RemoteCertificateSource = 'selfSigned' | 'store'

export interface RemoteAccessSettings {
  enabled: boolean
  hostNames: string[]
  certificateSource: RemoteCertificateSource
  certificateThumbprint: string | null
  firewallProfiles: FirewallProfile[]
  trustedProxies: string[]
}

export interface StoreCertificate {
  thumbprint: string
  subject: string
  notAfter: string
  names: string[]
}

export interface RemoteAccessView {
  settings: RemoteAccessSettings
  active: boolean
  restartRequired: boolean
  canRestart: boolean
  problem: string | null
  port: number
  urls: string[]
  suggestedHostNames: string[]
  certificates: StoreCertificate[]
  hasSelfSignedCertificate: boolean
}

export interface RemoteAccount {
  id: string
  userName: string
  ready: boolean
  createdAt: string
  createdBy: string
  lastSignInAt: string | null
  lockedUntil: string | null
  locked: boolean
  recoveryCodesLeft: number
}

export interface AccountSetup {
  account: RemoteAccount
  setup: { setupUri: string; secret: string; recoveryCodes: string[] }
}

const remoteKey = ['remoteAccess'] as const
const accountsKey = ['remoteAccounts'] as const

/** Remote access is managed only on this computer; from elsewhere the API answers 403 and the section hides. */
export function useRemoteAccess() {
  return useQuery({ queryKey: remoteKey, queryFn: () => getJson<RemoteAccessView>('/api/remote'), retry: false })
}

export function useSaveRemoteAccess() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (settings: RemoteAccessSettings) => request<RemoteAccessView>('PUT', '/api/remote', settings),
    onSuccess: (view) => queryClient.setQueryData(remoteKey, view),
  })
}

export function useRemoteAccounts(enabled: boolean) {
  return useQuery({ queryKey: accountsKey, queryFn: () => getJson<RemoteAccount[]>('/api/remote/accounts'), enabled })
}

export function useAccountActions() {
  const queryClient = useQueryClient()
  const refresh = () => Promise.all([queryClient.invalidateQueries({ queryKey: accountsKey }), queryClient.invalidateQueries({ queryKey: remoteKey })])
  const after = async <T>(promise: Promise<T>) => {
    const result = await promise
    await refresh()
    return result
  }
  return {
    create: (userName: string, password: string) => after(request<AccountSetup>('POST', '/api/remote/accounts', { userName, password })),
    confirm: (id: string, code: string) => after(request<void>('POST', `/api/remote/accounts/${id}/confirm`, { code })),
    resetAuthenticator: (id: string) => after(request<AccountSetup>('POST', `/api/remote/accounts/${id}/authenticator`)),
    setPassword: (id: string, password: string) => after(request<void>('PUT', `/api/remote/accounts/${id}/password`, { password })),
    remove: (id: string) => after(request<void>('DELETE', `/api/remote/accounts/${id}`)),
  }
}

export const restartService = () => request<void>('POST', '/api/system/restart')

// ---- Database logins for dumps ---------------------------------------------

export interface DatabaseCredential {
  containerName: string
  appId: string
  kind: DatabaseKind
  user: string
  database: string | null
  updatedAt: string
}

const credentialsKey = ['databaseCredentials'] as const

export function useDatabaseCredentials() {
  return useQuery({ queryKey: credentialsKey, queryFn: () => getJson<DatabaseCredential[]>('/api/databases/credentials'), staleTime: 60_000 })
}

export function useDatabaseCredentialActions() {
  const queryClient = useQueryClient()
  const refresh = () => queryClient.invalidateQueries({ queryKey: credentialsKey })
  const path = (container: string) => `/api/databases/credentials/${encodeURIComponent(container)}`
  return {
    save: async (container: string, input: { user: string; password: string; database: string }) => {
      await request<void>('PUT', path(container), { user: input.user, password: input.password || null, database: input.database || null })
      await refresh()
    },
    remove: async (container: string) => {
      await request<void>('DELETE', path(container))
      await refresh()
    },
    test: (container: string, input?: { user: string; password: string; database: string }) =>
      request<{ ok: boolean; error: string | null }>(
        'POST',
        `${path(container)}/test`,
        input ? { user: input.user, password: input.password || null, database: input.database || null } : {},
      ),
  }
}

/** 401/403: not signed in, or no access. Retrying or polling won't change that until the user acts. */
export const isAuthError = (error: unknown) => error instanceof ApiError && (error.status === 401 || error.status === 403)

export function useSystemInfo() {
  return useQuery({
    queryKey: queryKeys.system,
    queryFn: () => getJson<SystemInfo>('/api/system'),
    // While signed out, stop polling: a refetch of a query that never succeeded goes back to "pending",
    // which would swap the sign-in screen for the app shell and back, wiping what was typed.
    refetchInterval: (query) => (isAuthError(query.state.error) ? false : 15_000),
    retry: (count, error) => !isAuthError(error) && count < 3,
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

export function useIncludeFolder() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ path, include }: { path: string; include: boolean }) => request<void>('POST', '/api/inventory/folders', { path, include }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.inventory }),
  })
}

export function useDestinations() {
  return useQuery({
    queryKey: queryKeys.destinations,
    queryFn: () => getJson<Destination[]>('/api/destinations'),
    refetchInterval: 120_000,
  })
}

export function useDestinationTypes() {
  return useQuery({ queryKey: ['destinationTypes'], queryFn: () => getJson<DestinationType[]>('/api/destination-types'), staleTime: Infinity })
}

export function useCreateDestination() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: CreateDestination) => request<Destination>('POST', '/api/destinations', body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.destinations }),
  })
}

export function useUpdateDestination(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: UpdateDestination) => request<Destination>('PUT', `/api/destinations/${id}`, body),
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

export interface SignInSession {
  id: string
  state: 'pending' | 'ready' | 'failed'
  authUrl: string | null
  error: string | null
}

/** Browser sign-in to a cloud service (a destination type's signIn). */
export const startSignIn = (service: string) => request<SignInSession>('POST', `/api/${service}/connect`)
export const pollSignIn = (service: string, id: string) => request<SignInSession>('GET', `/api/${service}/connect/${id}`)
export const completeSignIn = (service: string, id: string, url: string) => request<SignInSession>('POST', `/api/${service}/connect/${id}/callback`, { url })

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

export function useStartDrill() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { destinationId: string; snapshotId: string }) => request<BackupRun>('POST', '/api/drills', body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.backups }),
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
