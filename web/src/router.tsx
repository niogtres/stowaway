import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router'
import { AppShell } from '@/components/AppShell'
import { Activity } from '@/pages/Activity'
import { Dashboard } from '@/pages/Dashboard'
import { Destinations } from '@/pages/Destinations'
import { Inventory } from '@/pages/Inventory'
import { Plans } from '@/pages/Plans'
import { Settings } from '@/pages/Settings'
import { Snapshots } from '@/pages/Snapshots'

const rootRoute = createRootRoute({ component: AppShell })

const snapshotsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/snapshots',
  validateSearch: (search: Record<string, unknown>): { app?: string } => ({ app: typeof search.app === 'string' ? search.app : undefined }),
  component: function SnapshotsPage() {
    const { app } = snapshotsRoute.useSearch()
    return <Snapshots key={app ?? ''} initialApp={app} />
  },
})

const routeTree = rootRoute.addChildren([
  createRoute({ getParentRoute: () => rootRoute, path: '/', component: Dashboard }),
  createRoute({ getParentRoute: () => rootRoute, path: '/inventory', component: Inventory }),
  createRoute({ getParentRoute: () => rootRoute, path: '/destinations', component: Destinations }),
  createRoute({ getParentRoute: () => rootRoute, path: '/activity', component: Activity }),
  createRoute({ getParentRoute: () => rootRoute, path: '/plans', component: Plans }),
  createRoute({ getParentRoute: () => rootRoute, path: '/settings', component: Settings }),
  snapshotsRoute,
])

export const router = createRouter({ routeTree, defaultPreload: 'intent' })

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
