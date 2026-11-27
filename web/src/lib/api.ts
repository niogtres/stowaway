import { useQuery } from '@tanstack/react-query'

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

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(path, { headers: { Accept: 'application/json' } })
  if (!response.ok) throw new ApiError(response.status, `${response.status} ${response.statusText}`)
  return (await response.json()) as T
}

export function useSystemInfo() {
  return useQuery({
    queryKey: ['system'],
    queryFn: () => getJson<SystemInfo>('/api/system'),
    refetchInterval: 10_000,
  })
}

/** Strips the build metadata (+commit) from an informational version. */
export function displayVersion(version: string) {
  return version.split('+')[0]
}
