import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router'
import { History } from 'lucide-react'
import { AppShell } from '@/components/AppShell'
import { ComingSoon } from '@/pages/ComingSoon'
import { Dashboard } from '@/pages/Dashboard'
import { Activity } from '@/pages/Activity'
import { Destinations } from '@/pages/Destinations'
import { Inventory } from '@/pages/Inventory'
import { Plans } from '@/pages/Plans'
import { Settings } from '@/pages/Settings'
import emptySnapshots from '@/assets/illustrations/empty-snapshots.webp'

const rootRoute = createRootRoute({ component: AppShell })

const placeholders = [
  {
    path: '/snapshots',
    icon: History,
    title: 'Snapshots',
    milestone: 'M4',
    illustration: emptySnapshots,
    description: 'Scrub back through time, browse files and restore a whole stack, one volume or a single file.',
  },
]

const routeTree = rootRoute.addChildren([
  createRoute({ getParentRoute: () => rootRoute, path: '/', component: Dashboard }),
  createRoute({ getParentRoute: () => rootRoute, path: '/inventory', component: Inventory }),
  createRoute({ getParentRoute: () => rootRoute, path: '/destinations', component: Destinations }),
  createRoute({ getParentRoute: () => rootRoute, path: '/activity', component: Activity }),
  createRoute({ getParentRoute: () => rootRoute, path: '/plans', component: Plans }),
  createRoute({ getParentRoute: () => rootRoute, path: '/settings', component: Settings }),
  ...placeholders.map(({ path, ...props }) =>
    createRoute({ getParentRoute: () => rootRoute, path, component: () => <ComingSoon {...props} /> }),
  ),
])

export const router = createRouter({ routeTree, defaultPreload: 'intent' })

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
