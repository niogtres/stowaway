import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router'
import { Activity, CalendarClock, HardDrive, History, Settings } from 'lucide-react'
import { AppShell } from '@/components/AppShell'
import { ComingSoon } from '@/pages/ComingSoon'
import { Dashboard } from '@/pages/Dashboard'
import { Inventory } from '@/pages/Inventory'
import emptyPlans from '@/assets/illustrations/empty-plans.webp'
import emptySnapshots from '@/assets/illustrations/empty-snapshots.webp'

const rootRoute = createRootRoute({ component: AppShell })

const placeholders = [
  {
    path: '/plans',
    icon: CalendarClock,
    title: 'Backup plans',
    milestone: 'M3',
    illustration: emptyPlans,
    description: 'Choose what to protect, how to keep it consistent, where it goes and when it runs.',
  },
  {
    path: '/snapshots',
    icon: History,
    title: 'Snapshots',
    milestone: 'M4',
    illustration: emptySnapshots,
    description: 'Scrub back through time, browse files and restore a whole stack, one volume or a single file.',
  },
  {
    path: '/activity',
    icon: Activity,
    title: 'Activity',
    milestone: 'M3',
    description: 'Live job progress with step timelines and streaming logs, plus a searchable history.',
  },
  {
    path: '/destinations',
    icon: HardDrive,
    title: 'Destinations',
    milestone: 'M2',
    description: 'Local disks, network shares and cloud storage, encrypted and deduplicated.',
  },
  {
    path: '/settings',
    icon: Settings,
    title: 'Settings',
    milestone: 'M2',
    description: 'Notifications, engine connection, limits and security.',
  },
]

const routeTree = rootRoute.addChildren([
  createRoute({ getParentRoute: () => rootRoute, path: '/', component: Dashboard }),
  createRoute({ getParentRoute: () => rootRoute, path: '/inventory', component: Inventory }),
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
