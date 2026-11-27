import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { Activity, Boxes, CalendarClock, HardDrive, History, LayoutDashboard, Settings, type LucideIcon } from 'lucide-react'
import logoMark from '@/assets/logo-mark.svg'
import { displayVersion, useSystemInfo } from '@/lib/api'
import { useRealtime } from '@/lib/realtime'
import { EngineStatusPill } from './EngineStatusPill'
import { ThemeToggle } from './ThemeToggle'
import { StatusDot } from './ui'

interface NavItem {
  to: string
  label: string
  icon: LucideIcon
}

const navItems: NavItem[] = [
  { to: '/', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/inventory', label: 'Inventory', icon: Boxes },
  { to: '/plans', label: 'Plans', icon: CalendarClock },
  { to: '/snapshots', label: 'Snapshots', icon: History },
  { to: '/activity', label: 'Activity', icon: Activity },
  { to: '/destinations', label: 'Destinations', icon: HardDrive },
  { to: '/settings', label: 'Settings', icon: Settings },
]

export function AppShell() {
  const pathname = useRouterState({ select: (s) => s.location.pathname })
  const current = navItems.find((item) => (item.to === '/' ? pathname === '/' : pathname.startsWith(item.to)))
  const { data } = useSystemInfo()
  const realtime = useRealtime()

  return (
    <div className="relative flex h-full">
      <div className="aurora pointer-events-none fixed inset-0 -z-10" aria-hidden />

      <aside className="flex w-60 shrink-0 flex-col border-r border-line px-3 py-5">
        <div className="mb-8 flex items-center gap-2.5 px-2">
          <img src={logoMark} alt="" className="size-8" />
          <div className="leading-tight">
            <div className="font-semibold tracking-tight">DockerBackup</div>
            <div className="text-[11px] text-muted">{data ? `v${displayVersion(data.version)}` : ' '}</div>
          </div>
        </div>

        <nav className="flex flex-col gap-0.5" aria-label="Main">
          {navItems.map(({ to, label, icon: Icon }) => (
            <Link
              key={to}
              to={to}
              activeOptions={{ exact: to === '/' }}
              className="group flex items-center gap-3 rounded-xl px-3 py-2 text-sm text-muted transition hover:bg-accent-soft hover:text-fg data-[status=active]:bg-accent-soft data-[status=active]:text-fg"
            >
              <Icon className="size-4 transition group-data-[status=active]:text-accent" />
              {label}
            </Link>
          ))}
        </nav>

        <div className="mt-auto px-2 text-[11px] text-muted">
          {data && (
            <>
              <div className="truncate">{data.machineName}</div>
              <div>{data.runningAsService ? 'Windows Service' : 'Console mode'}</div>
            </>
          )}
          <div className="mt-2 flex items-center gap-1.5" title="Live updates from the service">
            <StatusDot tone={realtime === 'live' ? 'ok' : realtime === 'connecting' ? 'warn' : 'muted'} />
            {realtime === 'live' ? 'Live' : realtime === 'connecting' ? 'Connecting…' : 'Offline'}
          </div>
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-between gap-4 px-8 pb-2 pt-6">
          <h1 className="text-xl font-semibold tracking-tight">{current?.label ?? 'DockerBackup'}</h1>
          <div className="flex items-center gap-2">
            <EngineStatusPill />
            <ThemeToggle />
          </div>
        </header>
        <main className="min-h-0 flex-1 overflow-y-auto px-8 pb-10 pt-4">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
