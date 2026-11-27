import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { Activity, Boxes, CalendarClock, HardDrive, History, LayoutDashboard, LogOut, Settings, type LucideIcon } from 'lucide-react'
import logoMark from '@/assets/logo-mark.svg'
import { ApiError, displayVersion, signOut, useAuthMode, useMe, useSystemInfo } from '@/lib/api'
import { useRealtime } from '@/lib/realtime'
import { EngineStatusPill } from './EngineStatusPill'
import { FreshCodePrompt } from './FreshCode'
import { SignIn } from './SignIn'
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
  const { data, error } = useSystemInfo()
  const me = useMe()
  const realtime = useRealtime()
  const blocked = error instanceof ApiError && (error.status === 401 || error.status === 403) ? error.status : null
  const mode = useAuthMode(blocked !== null)

  if (blocked !== null) {
    if (mode.isPending) return <div className="aurora pointer-events-none fixed inset-0 -z-10" aria-hidden />
    if (blocked === 401 && mode.data?.signIn === 'account') return <SignIn />
    return <AccessDenied forbidden={blocked === 403} />
  }

  return (
    <div className="relative flex h-full">
      <div className="aurora pointer-events-none fixed inset-0 -z-10" aria-hidden />

      <aside className="flex w-60 shrink-0 flex-col border-r border-line px-3 py-5">
        <div className="mb-8 flex items-center gap-2.5 px-2">
          <img src={logoMark} alt="" className="size-8" />
          <div className="leading-tight">
            <div className="font-semibold tracking-tight">Stowaway</div>
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
          {me.data?.signInRequired && (
            <div className="flex items-center gap-1">
              <span className="truncate" title={me.data.method === 'account' ? 'Signed in remotely' : 'Signed in with Windows'}>
                {me.data.name}
              </span>
              {me.data.method === 'account' && (
                <button
                  type="button"
                  className="ml-auto rounded p-0.5 transition hover:text-fg"
                  title="Sign out"
                  aria-label="Sign out"
                  onClick={async () => {
                    await signOut()
                    window.location.reload()
                  }}
                >
                  <LogOut className="size-3.5" />
                </button>
              )}
            </div>
          )}
          <div className="mt-2 flex items-center gap-1.5" title="Live updates from the service">
            <StatusDot tone={realtime === 'live' ? 'ok' : realtime === 'connecting' ? 'warn' : 'muted'} />
            {realtime === 'live' ? 'Live' : realtime === 'connecting' ? 'Connecting…' : 'Offline'}
          </div>
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-between gap-4 px-8 pb-2 pt-6">
          <h1 className="text-xl font-semibold tracking-tight">{current?.label ?? 'Stowaway'}</h1>
          <div className="flex items-center gap-2">
            <EngineStatusPill />
            <ThemeToggle />
          </div>
        </header>
        <main className="min-h-0 flex-1 overflow-y-auto px-8 pb-10 pt-4">
          <Outlet />
        </main>
        {me.data?.method === 'account' && <FreshCodePrompt />}
      </div>
    </div>
  )
}

function AccessDenied({ forbidden }: { forbidden: boolean }) {
  return (
    <div className="relative grid h-full place-items-center px-6">
      <div className="aurora pointer-events-none fixed inset-0 -z-10" aria-hidden />
      <div className="glass max-w-md rounded-2xl p-8 text-center">
        <img src={logoMark} alt="" className="mx-auto mb-4 size-12" />
        <h1 className="text-lg font-semibold tracking-tight">{forbidden ? 'No access' : 'Sign-in required'}</h1>
        <p className="mt-2 text-sm text-muted">
          {forbidden
            ? 'Stowaway is available to local administrators and members of the "Stowaway Operators" group. Ask an administrator to add your Windows account to that group.'
            : 'Stowaway signs you in with your Windows account. If your browser asks, enter your Windows user name and password.'}
        </p>
      </div>
    </div>
  )
}
