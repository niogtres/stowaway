import { createRootRoute, createRoute, createRouter, useRouterState } from '@tanstack/react-router'
import { AppShell } from '@/components/AppShell'
import { About } from '@/pages/About'
import { Activity } from '@/pages/Activity'
import { Dashboard } from '@/pages/Dashboard'
import { Destinations } from '@/pages/Destinations'
import { Help } from '@/pages/Help'
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

const helpTopicRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/help/$slug',
  component: function HelpTopicPage() {
    const { slug } = helpTopicRoute.useParams()
    const hash = useRouterState({ select: (s) => s.location.hash })
    return <Help slug={slug} hash={hash || undefined} />
  },
})

const routeTree = rootRoute.addChildren([
  createRoute({ getParentRoute: () => rootRoute, path: '/', component: Dashboard }),
  createRoute({ getParentRoute: () => rootRoute, path: '/inventory', component: Inventory }),
  createRoute({ getParentRoute: () => rootRoute, path: '/destinations', component: Destinations }),
  createRoute({ getParentRoute: () => rootRoute, path: '/activity', component: Activity }),
  createRoute({ getParentRoute: () => rootRoute, path: '/plans', component: Plans }),
  createRoute({ getParentRoute: () => rootRoute, path: '/settings', component: Settings }),
  createRoute({ getParentRoute: () => rootRoute, path: '/help', component: () => <Help /> }),
  helpTopicRoute,
  createRoute({ getParentRoute: () => rootRoute, path: '/about', component: About }),
  snapshotsRoute,
])

export const router = createRouter({ routeTree, defaultPreload: 'intent' })

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
