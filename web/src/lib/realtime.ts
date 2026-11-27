import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { queryKeys, type BackupProgress } from './api'

export type RealtimeState = 'connecting' | 'live' | 'offline'

/**
 * Subscribes to server events for the lifetime of the app and turns them into query invalidations,
 * so views refetch exactly when something changed.
 */
export function useRealtime(): RealtimeState {
  const queryClient = useQueryClient()
  const [state, setState] = useState<RealtimeState>('connecting')

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/events')
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30_000, 1_000 * 2 ** ctx.previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('inventoryUpdated', () => queryClient.invalidateQueries({ queryKey: queryKeys.inventory }))
    connection.on('destinationsChanged', () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.destinations })
      queryClient.invalidateQueries({ queryKey: queryKeys.snapshots })
    })
    connection.on('plansChanged', () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.plans })
      queryClient.invalidateQueries({ queryKey: queryKeys.inventory })
    })
    connection.on('backupUpdated', (update: { id: string; status: string }) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.backups })
      queryClient.invalidateQueries({ queryKey: queryKeys.plans })
      if (update.status !== 'running' && update.status !== 'queued') {
        queryClient.setQueryData<Record<string, BackupProgress>>(queryKeys.progress, (current = {}) => {
          const rest = { ...current }
          delete rest[update.id]
          return rest
        })
      }
    })
    connection.on('backupProgress', (progress: BackupProgress) =>
      queryClient.setQueryData<Record<string, BackupProgress>>(queryKeys.progress, (current = {}) => ({
        ...current,
        [progress.runId]: progress,
      })),
    )
    connection.on('engineStatusChanged', () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.system })
      queryClient.invalidateQueries({ queryKey: queryKeys.inventory })
    })
    connection.onreconnecting(() => setState('connecting'))
    connection.onreconnected(() => {
      setState('live')
      // We may have missed events while disconnected.
      queryClient.invalidateQueries()
    })
    connection.onclose(() => setState('offline'))

    let disposed = false
    const start = async (attempt = 0): Promise<void> => {
      try {
        await connection.start()
        if (!disposed) setState('live')
      } catch (error) {
        if (disposed) return
        setState('offline')
        // Not signed in: retrying can't help; the page reloads after signing in anyway.
        const status = (error as { statusCode?: number }).statusCode
        if (status === 401 || status === 403) return
        setTimeout(() => void start(attempt + 1), Math.min(30_000, 1_000 * 2 ** attempt))
      }
    }
    void start()

    return () => {
      disposed = true
      if (connection.state !== HubConnectionState.Disconnected) void connection.stop()
    }
  }, [queryClient])

  return state
}
