// Made-up data for the help screenshots: a home server running a few self-hosted apps. Nothing here comes from a
// real machine. Times are relative to "now", so "2 hours ago" stays true whenever the screenshots are taken.

const GB = 1024 ** 3
const MB = 1024 ** 2
const now = () => Date.now()
const ago = (minutes) => new Date(now() - minutes * 60_000).toISOString()
const ahead = (minutes) => new Date(now() + minutes * 60_000).toISOString()
const id = (n) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`

export const machine = 'HOMESERVER'

// ---- Inventory ------------------------------------------------------------------------------------------------

const mount = (m) => ({
  kind: 'volume',
  readOnly: false,
  volumeName: null,
  anonymous: false,
  included: true,
  exclusionReason: null,
  sizeBytes: null,
  canInclude: false,
  includedByOwner: false,
  note: null,
  notNeeded: false,
  ...m,
})
const volume = (name, destination, sizeBytes) => mount({ source: name, displaySource: name, destination, volumeName: name, sizeBytes })
const bind = (path, destination, extra = {}) => mount({ kind: 'bind', source: path, displaySource: path, destination, ...extra })

const container = (c) => ({
  id: `c-${c.name}`,
  imageId: `sha256:${c.name}`,
  imageIsLocalOnly: false,
  state: 'running',
  statusText: 'Up 6 days',
  health: null,
  createdAt: ago(60 * 24 * 40),
  composeService: null,
  database: null,
  product: null,
  dumpSupported: false,
  mounts: [],
  ...c,
})

const app = (a) => ({
  kind: 'compose',
  composeWorkingDir: null,
  composeConfigFiles: [],
  databaseBytes: null,
  sizeIncomplete: false,
  platform: 'linux',
  ...a,
  id: `${a.kind === 'standalone' ? 'container' : 'compose'}:${a.name}`,
  dataBytes: a.dataBytes ?? a.containers.flatMap((c) => c.mounts).reduce((sum, m) => sum + (m.included ? (m.sizeBytes ?? 0) : 0), 0),
})

const linuxApps = [
  app({
    name: 'gitea',
    protection: 'protected',
    composeWorkingDir: 'D:\\docker\\gitea',
    composeConfigFiles: ['D:\\docker\\gitea\\compose.yaml'],
    containers: [
      container({ name: 'gitea', image: 'gitea/gitea:1.24', composeService: 'gitea', health: 'healthy', mounts: [volume('gitea_data', '/data', 3.2 * GB)] }),
      container({
        name: 'gitea-db',
        image: 'mariadb:11',
        composeService: 'db',
        database: 'mariaDb',
        dumpSupported: true,
        mounts: [volume('gitea_db', '/var/lib/mysql', 412 * MB)],
      }),
    ],
  }),
  app({
    name: 'homeassistant',
    protection: 'stale',
    composeWorkingDir: 'D:\\docker\\homeassistant',
    composeConfigFiles: ['D:\\docker\\homeassistant\\compose.yaml'],
    containers: [
      container({
        name: 'homeassistant',
        image: 'ghcr.io/home-assistant/home-assistant:stable',
        composeService: 'homeassistant',
        mounts: [bind('D:\\docker\\homeassistant\\config', '/config', { sizeBytes: 640 * MB })],
      }),
    ],
  }),
  app({
    name: 'jellyfin',
    kind: 'standalone',
    protection: 'protected',
    containers: [
      container({
        name: 'jellyfin',
        image: 'jellyfin/jellyfin:10.11',
        mounts: [
          volume('jellyfin_config', '/config', 1.1 * GB),
          bind('M:\\', '/media', { included: false, exclusionReason: 'Whole drive: outside Docker. Back it up with your file backup.' }),
        ],
      }),
    ],
  }),
  app({
    name: 'nextcloud',
    protection: 'protected',
    composeWorkingDir: 'D:\\docker\\nextcloud',
    composeConfigFiles: ['D:\\docker\\nextcloud\\compose.yaml'],
    containers: [
      container({ name: 'nextcloud-app', image: 'nextcloud:31-apache', composeService: 'app', health: 'healthy', mounts: [volume('nextcloud_html', '/var/www/html', 12.4 * GB)] }),
      container({
        name: 'nextcloud-db',
        image: 'postgres:17',
        composeService: 'db',
        database: 'postgreSql',
        dumpSupported: true,
        mounts: [volume('nextcloud_db', '/var/lib/postgresql/data', 830 * MB)],
      }),
      container({ name: 'nextcloud-redis', image: 'redis:8-alpine', composeService: 'redis', database: 'redis', dumpSupported: true }),
    ],
  }),
  app({
    name: 'paperless',
    protection: 'unprotected',
    composeWorkingDir: 'D:\\docker\\paperless',
    composeConfigFiles: ['D:\\docker\\paperless\\compose.yaml'],
    containers: [
      container({
        name: 'paperless-web',
        image: 'ghcr.io/paperless-ngx/paperless-ngx:2.18',
        composeService: 'webserver',
        mounts: [volume('paperless_data', '/usr/src/paperless/data', 210 * MB), volume('paperless_media', '/usr/src/paperless/media', 2.1 * GB)],
      }),
      container({
        name: 'paperless-db',
        image: 'postgres:17',
        composeService: 'db',
        database: 'postgreSql',
        dumpSupported: true,
        mounts: [volume('paperless_pgdata', '/var/lib/postgresql/data', 96 * MB)],
      }),
    ],
  }),
  app({
    name: 'uptime-kuma',
    kind: 'standalone',
    protection: 'protected',
    containers: [container({ name: 'uptime-kuma', image: 'louislam/uptime-kuma:2', mounts: [volume('uptime-kuma', '/app/data', 58 * MB)] })],
  }),
]

const bcFolder = 'C:\\ProgramData\\BcContainerHelper\\Extensions\\bc-demo\\my'
const windowsApps = [
  app({
    name: 'bc-demo',
    kind: 'standalone',
    protection: 'unprotected',
    platform: 'windows',
    sizeIncomplete: true,
    containers: [
      container({
        name: 'bc-demo',
        image: 'mcr.microsoft.com/businesscentral:ltsc2025',
        state: 'exited',
        statusText: 'Exited (0) 3 hours ago',
        database: 'sqlServer',
        dumpSupported: true,
        product: {
          profile: 'business-central',
          name: 'Business Central',
          version: '26.4.38012.0',
          country: 'W1',
          note: "Business Central: the database and the container's own folder are backed up, with a script that recreates the container from the snapshot (see Help, Recover a whole app).",
        },
        mounts: [
          bind('C:\\bcartifacts.cache', 'c:\\dl', {
            included: false,
            canInclude: true,
            notNeeded: true,
            exclusionReason: 'Business Central artifact cache: downloaded again when a container needs it, so it needs no backup.',
          }),
          bind('C:\\ProgramData\\BcContainerHelper', 'c:\\programdata\\bccontainerhelper', {
            included: false,
            canInclude: true,
            notNeeded: true,
            exclusionReason:
              "BcContainerHelper's own folder, shared by every Business Central container on this computer. Each container's part of it (its my folder) is backed up with that container; BcContainerHelper sets up the rest again.",
          }),
          bind(bcFolder, 'c:\\run\\my', { sizeBytes: 2.3 * MB, note: "Business Central: this container's own setup scripts and certificates" }),
          bind('C:\\windows\\system32\\drivers\\etc', 'c:\\driversetc', {
            included: false,
            notNeeded: true,
            exclusionReason: "The computer's hosts file, which BcContainerHelper updates so the container can be reached by name. Nothing to back up.",
          }),
        ],
      }),
    ],
  }),
]

const volumesOf = (apps) =>
  apps.flatMap((a) =>
    a.containers.flatMap((c) =>
      c.mounts
        .filter((m) => m.kind === 'volume')
        .map((m) => ({
          name: m.volumeName,
          driver: 'local',
          createdAt: ago(60 * 24 * 60),
          anonymous: false,
          orphaned: false,
          composeProject: a.kind === 'compose' ? a.name : null,
          usedBy: [c.name],
          sizeBytes: m.sizeBytes,
          protection: a.protection,
          exclusionReason: null,
        })),
    ),
  )

function totals(apps, volumes) {
  const containers = apps.flatMap((a) => a.containers)
  const holdsData = (s) => s !== 'stateless' && s !== 'excluded'
  const dataBytes = apps.reduce((sum, a) => sum + a.dataBytes, 0)
  const protectedBytes = apps.filter((a) => a.protection === 'protected').reduce((sum, a) => sum + a.dataBytes, 0)
  const dataItems = apps.filter((a) => holdsData(a.protection)).length
  const protectedItems = apps.filter((a) => a.protection === 'protected').length
  return {
    apps: apps.length,
    containers: containers.length,
    runningContainers: containers.filter((c) => c.state === 'running').length,
    volumes: volumes.length,
    orphanedVolumes: 0,
    bindMounts: containers.flatMap((c) => c.mounts).filter((m) => m.kind === 'bind').length,
    databases: containers.filter((c) => c.database).length,
    localOnlyImages: 0,
    unprotectedItems: apps.filter((a) => a.protection === 'unprotected').length,
    failingItems: apps.filter((a) => a.protection === 'failing').length,
    staleItems: apps.filter((a) => a.protection === 'stale').length,
    dataBytes,
    protectedBytes,
    dataItems,
    protectedItems,
    protectionScore: dataBytes > 0 ? Math.floor((protectedBytes * 100) / dataBytes) : dataItems > 0 ? Math.floor((protectedItems * 100) / dataItems) : null,
  }
}

export function inventory(scenario) {
  const apps = scenario === 'windows' ? windowsApps : linuxApps
  const volumes = volumesOf(apps)
  return { engineReachable: true, snapshot: { version: 42, capturedAt: ago(1), apps, volumes, totals: totals(apps, volumes) } }
}

export function system(scenario) {
  const windows = scenario === 'windows'
  return {
    product: 'Stowaway',
    version: '0.12.0',
    machineName: machine,
    operatingSystem: 'Microsoft Windows 11 Pro 10.0.26100',
    runningAsService: true,
    startedAt: ago(60 * 26),
    engine: {
      reachable: true,
      endpoint: windows ? 'npipe://./pipe/docker_engine' : 'npipe://./pipe/dockerDesktopLinuxEngine',
      serverVersion: '29.1.2',
      apiVersion: '1.52',
      osType: windows ? 'windows' : 'linux',
      operatingSystem: windows ? 'Windows Server 2025' : 'Docker Desktop',
      hostName: windows ? machine : 'docker-desktop',
      containers: windows ? 1 : 10,
      containersRunning: windows ? 0 : 10,
      containersStopped: windows ? 1 : 0,
      images: windows ? 3 : 14,
      error: null,
      checkedAt: ago(0),
    },
  }
}

// ---- Destinations ---------------------------------------------------------------------------------------------

export const destinations = () => [
  {
    id: id(1),
    name: 'USB disk',
    kind: 'local',
    path: 'E:\\Backups\\homeserver',
    smbUsername: null,
    smbDomain: null,
    repositoryId: '3f9c1e07a2',
    createdAt: ago(60 * 24 * 90),
    status: { reachable: true, error: null, snapshots: 214, storedBytes: 21.7 * GB, lastSnapshotAt: ago(185), checkedAt: ago(4) },
    settings: { path: 'E:\\Backups\\homeserver' },
    recoveryKeyConfirmedAt: ago(60 * 24 * 90),
    recoveryKeyFingerprint: '4b1e9a07',
  },
  {
    id: id(2),
    name: 'Backup server',
    kind: 'rest',
    path: 'https://backup.example.net:8000/homeserver (append-only)',
    smbUsername: null,
    smbDomain: null,
    repositoryId: 'a7d2c95e31',
    createdAt: ago(60 * 24 * 30),
    status: { reachable: true, error: null, snapshots: 61, storedBytes: 19.8 * GB, lastSnapshotAt: ago(60 * 24 * 2), checkedAt: ago(4) },
    settings: { url: 'https://backup.example.net:8000/homeserver', appendOnly: 'true', user: 'homeserver' },
    recoveryKeyConfirmedAt: ago(60 * 24 * 30),
    recoveryKeyFingerprint: 'c02f71d8',
  },
  {
    id: id(3),
    name: 'Offsite',
    kind: 'b2',
    path: 'b2://homeserver-backups/docker',
    smbUsername: null,
    smbDomain: null,
    repositoryId: '9e04b7f1c6',
    createdAt: ago(60 * 3),
    status: { reachable: true, error: null, snapshots: 0, storedBytes: 0, lastSnapshotAt: null, checkedAt: ago(3) },
    settings: { bucket: 'homeserver-backups', prefix: 'docker' },
    recoveryKeyConfirmedAt: null,
    recoveryKeyFingerprint: '5d83e2b4',
  },
]

export const recoveryKey = 'k7Qm-2xVt-9pLc-Hs4R-wN8e-Fb3Y'

export const kit = (d) => `STOWAWAY RECOVERY KIT\r\n\r\nDestination: ${d.name}\r\nRecovery key: ${recoveryKey}\r\n`

const field = (f) => ({
  type: 'text',
  section: 'location',
  required: false,
  secret: false,
  hidden: false,
  placeholder: null,
  hint: null,
  default: null,
  mono: false,
  half: false,
  ...f,
})

export const destinationTypes = [
  {
    kind: 'local',
    label: 'Local folder',
    icon: 'folder',
    namePlaceholder: 'USB disk',
    fields: [field({ key: 'path', label: 'Folder', required: true, mono: true, placeholder: 'E:\\Backups\\docker', hint: 'A folder on this computer: a second disk or a USB disk.' })],
    signIn: null,
    notes: null,
  },
  { kind: 'smb', label: 'Network share', icon: 'network', namePlaceholder: 'NAS', fields: [], signIn: null, notes: null },
  { kind: 's3', label: 'S3 / compatible', icon: 'cloud', namePlaceholder: 'S3', fields: [], signIn: null, notes: null },
  { kind: 'b2', label: 'Backblaze B2', icon: 'hardDriveUpload', namePlaceholder: 'Backblaze', fields: [], signIn: null, notes: null },
  { kind: 'azure', label: 'Azure Blob', icon: 'database', namePlaceholder: 'Azure', fields: [], signIn: null, notes: null },
  { kind: 'sftp', label: 'SFTP server', icon: 'server', namePlaceholder: 'Offsite server', fields: [], signIn: null, notes: null },
  { kind: 'dropbox', label: 'Dropbox', icon: 'box', namePlaceholder: 'Dropbox', fields: [], signIn: 'dropbox', notes: null },
  { kind: 'rest', label: 'REST server', icon: 'shield', namePlaceholder: 'Backup server', fields: [], signIn: null, notes: null },
  { kind: 'gcs', label: 'Google Cloud Storage', icon: 'cloud', namePlaceholder: 'Google Cloud', fields: [], signIn: null, notes: null },
  { kind: 'oneDrive', label: 'OneDrive', icon: 'cloud', namePlaceholder: 'OneDrive', fields: [], signIn: 'onedrive', notes: null },
]

// ---- Plans and runs ---------------------------------------------------------------------------------------------

const retention = { keepLast: 0, keepHourly: 0, keepDaily: 7, keepWeekly: 4, keepMonthly: 6, keepYearly: 0 }

export const plans = () => [
  {
    id: id(11),
    name: 'Nightly',
    enabled: true,
    selection: 'allApps',
    appIds: [],
    excludedAppIds: ['compose:paperless'],
    includeOrphanedVolumes: true,
    strategy: 'auto',
    destinationId: id(1),
    cron: '0 2 * * *',
    retention,
    verifyRestores: true,
    createdAt: ago(60 * 24 * 90),
    nextRunAt: ahead(60 * 9),
    intervalHours: 24,
    coveredItemIds: ['compose:gitea', 'compose:homeassistant', 'container:jellyfin', 'compose:nextcloud', 'container:uptime-kuma'],
    recentRuns: [
      { id: id(101), status: 'succeeded', appName: 'nextcloud', queuedAt: ago(185), finishedAt: ago(178), error: null },
      { id: id(102), status: 'succeeded', appName: 'gitea', queuedAt: ago(186), finishedAt: ago(184), error: null },
    ],
  },
  {
    id: id(12),
    name: 'Weekly offsite',
    enabled: true,
    selection: 'selected',
    appIds: ['compose:nextcloud', 'compose:gitea'],
    excludedAppIds: [],
    includeOrphanedVolumes: false,
    strategy: 'auto',
    destinationId: id(2),
    cron: '0 4 * * 0',
    retention: { ...retention, keepDaily: 0, keepWeekly: 8, keepMonthly: 12 },
    verifyRestores: false,
    createdAt: ago(60 * 24 * 30),
    nextRunAt: ahead(60 * 24 * 3 + 120),
    intervalHours: 168,
    coveredItemIds: ['compose:nextcloud', 'compose:gitea'],
    recentRuns: [{ id: id(111), status: 'succeeded', appName: 'nextcloud', queuedAt: ago(60 * 24 * 2), finishedAt: ago(60 * 24 * 2 - 21), error: null }],
  },
]

const run = (r) => ({
  destinationId: id(1),
  strategy: 'live',
  kind: 'backup',
  planId: id(11),
  trigger: 'schedule',
  snapshotsForgotten: 1,
  status: 'succeeded',
  startedAt: null,
  finishedAt: null,
  snapshotId: null,
  filesProcessed: 0,
  bytesProcessed: 0,
  bytesAdded: 0,
  downtimeSeconds: null,
  error: null,
  log: '',
  ...r,
})

export const runningRunId = id(200)

export const runs = () => [
  run({
    id: runningRunId,
    appId: 'compose:homeassistant',
    appName: 'homeassistant',
    trigger: 'manual',
    planId: null,
    status: 'running',
    queuedAt: ago(2),
    startedAt: ago(2),
    snapshotsForgotten: null,
    filesProcessed: null,
    bytesProcessed: null,
    bytesAdded: null,
  }),
  run({
    id: id(101),
    appId: 'compose:nextcloud',
    appName: 'nextcloud',
    queuedAt: ago(185),
    startedAt: ago(185),
    finishedAt: ago(178),
    snapshotId: 'e41b7c2a9f',
    filesProcessed: 48211,
    bytesProcessed: 13.2 * GB,
    bytesAdded: 186 * MB,
    log: 'Dumping nextcloud-db (PostgreSQL)\nBacking up 2 volumes\nRetention: 1 old snapshot(s) forgotten',
  }),
  run({
    id: id(102),
    appId: 'compose:gitea',
    appName: 'gitea',
    queuedAt: ago(186),
    startedAt: ago(186),
    finishedAt: ago(184),
    snapshotId: '7c90e3d1b4',
    filesProcessed: 9120,
    bytesProcessed: 3.6 * GB,
    bytesAdded: 12 * MB,
  }),
  run({
    id: id(103),
    appId: 'container:jellyfin',
    appName: 'jellyfin',
    queuedAt: ago(187),
    startedAt: ago(187),
    finishedAt: ago(186),
    snapshotId: '2d5a8f0c6e',
    filesProcessed: 3880,
    bytesProcessed: 1.1 * GB,
    bytesAdded: 41 * MB,
  }),
  run({
    id: id(104),
    appId: 'compose:nextcloud',
    appName: 'nextcloud',
    kind: 'drill',
    trigger: 'schedule',
    queuedAt: ago(60 * 20),
    startedAt: ago(60 * 20),
    finishedAt: ago(60 * 20 - 6),
    snapshotsForgotten: null,
    filesProcessed: 48211,
    bytesProcessed: 13.2 * GB,
    log: 'Restored the latest snapshot into scratch space\nLoaded the PostgreSQL dump into a throwaway container: 41 tables',
  }),
  run({
    id: id(105),
    appId: 'container:uptime-kuma',
    appName: 'uptime-kuma',
    queuedAt: ago(188),
    startedAt: ago(188),
    finishedAt: ago(187),
    snapshotId: 'b3e6f9a2c1',
    filesProcessed: 212,
    bytesProcessed: 58 * MB,
    bytesAdded: 2 * MB,
  }),
]

export const progress = (tick) => {
  const total = 640 * MB
  const done = Math.min(total, (0.38 + 0.01 * (tick % 30)) * total)
  return { runId: runningRunId, phase: 'backing up', percent: (done / total) * 100, bytesDone: done, totalBytes: total, filesDone: Math.round(1840 * (done / total)), totalFiles: 1840 }
}

// ---- Snapshots ------------------------------------------------------------------------------------------------

export const snapshots = () =>
  [
    ['compose:nextcloud', 185, 'e41b7c2a9f', 13.2 * GB, 186 * MB],
    ['compose:nextcloud', 185 + 1440, '0a8d4e2f71', 13.1 * GB, 142 * MB],
    ['compose:nextcloud', 185 + 2880, 'f2c7a90b38', 13.0 * GB, 208 * MB],
    ['compose:nextcloud', 185 + 4320, '6be1d03c95', 13.0 * GB, 97 * MB],
    ['compose:gitea', 186, '7c90e3d1b4', 3.6 * GB, 12 * MB],
    ['compose:gitea', 186 + 1440, '1f4a7b2e60', 3.6 * GB, 8 * MB],
    ['container:jellyfin', 187, '2d5a8f0c6e', 1.1 * GB, 41 * MB],
    ['container:uptime-kuma', 188, 'b3e6f9a2c1', 58 * MB, 2 * MB],
    ['compose:homeassistant', 60 * 24 * 4, '93c0e7a5d2', 640 * MB, 18 * MB],
  ].map(([appId, minutes, short, total, added]) => ({
    destinationId: id(1),
    id: `${short}${'0'.repeat(54)}`,
    shortId: short.slice(0, 8),
    time: ago(minutes),
    host: machine,
    appId,
    planId: id(11),
    safety: false,
    totalBytes: total,
    dataAdded: added,
  }))

export function contents(snapshotId) {
  const s = snapshots().find((x) => x.id === snapshotId) ?? snapshots()[0]
  const a = linuxApps.find((x) => x.id === s.appId) ?? linuxApps[3]
  const sources = a.containers.flatMap((c) =>
    c.mounts
      .filter((m) => m.included)
      .map((m) => ({
        snapshotPath: `/data/${c.name}${m.destination}`,
        kind: m.kind,
        source: m.source,
        displaySource: m.displaySource,
        containerName: c.name,
        destination: m.destination,
        sizeBytes: m.sizeBytes,
      })),
  )
  if (a.containers.some((c) => c.database))
    sources.push({ snapshotPath: '/data/_dumps', kind: 'dumps', source: '_dumps', displaySource: 'Database dumps', containerName: '', destination: '/', sizeBytes: 74 * MB })
  sources.push({ snapshotPath: '/data/_stowaway', kind: 'definition', source: '_stowaway', displaySource: 'App definition', containerName: '', destination: '/', sizeBytes: 48 * 1024 })
  return { appId: a.id, appName: a.name, appKind: a.kind, sources }
}

export const restorePreview = () => ({
  items: [
    { source: 'nextcloud_html → /var/www/html', kind: 'volume', target: 'where it was (nextcloud_html)', snapshotBytes: 12.4 * GB },
    { source: 'nextcloud_db → /var/lib/postgresql/data', kind: 'volume', target: 'where it was (nextcloud_db)', snapshotBytes: 830 * MB },
  ],
  containersToStop: ['nextcloud-app', 'nextcloud-db'],
  deletesExtraFiles: true,
  safetySnapshot: true,
  warnings: [],
  containersToRecreate: null,
})

// ---- Settings -------------------------------------------------------------------------------------------------

export const channels = () => [
  { id: id(21), name: 'Phone', kind: 'ntfy', enabled: true, onFailure: true, onRecovery: true, onSuccess: false, target: 'ntfy.sh/homeserver-backups', createdAt: ago(60 * 24 * 60) },
  { id: id(22), name: 'Email', kind: 'email', enabled: true, onFailure: true, onRecovery: false, onSuccess: false, target: 'alerts@example.com', createdAt: ago(60 * 24 * 60) },
]

export const remote = () => ({
  settings: { enabled: false, hostNames: [], certificateSource: 'selfSigned', certificateThumbprint: null, firewallProfiles: ['private'], trustedProxies: [] },
  active: false,
  restartRequired: false,
  canRestart: true,
  problem: null,
  port: 8485,
  urls: [],
  suggestedHostNames: ['homeserver', 'homeserver.lan'],
  certificates: [],
  hasSelfSignedCertificate: false,
})
