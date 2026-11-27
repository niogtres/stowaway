import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { BookOpen, Bug, CodeXml, ExternalLink, ShieldCheck } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import logoMark from '@/assets/logo-mark.svg'
import { Button } from '@/components/forms'
import { Card } from '@/components/ui'
import { displayVersion, useSystemInfo } from '@/lib/api'

export const repositoryUrl = 'https://github.com/niogtres/stowaway'
export const copyright = 'Copyright © 2026 Thomas Kraft'

const builtWith: { name: string; what: string; license: string; url: string }[] = [
  { name: 'restic', what: 'Encrypted, deduplicated backups', license: 'BSD-2-Clause', url: 'https://restic.net' },
  { name: 'rclone', what: 'Dropbox and other cloud storage', license: 'MIT', url: 'https://rclone.org' },
  { name: '.NET and ASP.NET Core', what: 'The service', license: 'MIT', url: 'https://dotnet.microsoft.com' },
  { name: 'React, TanStack, Tailwind CSS', what: 'This interface', license: 'MIT', url: 'https://react.dev' },
  { name: 'Docker.DotNet', what: 'Talking to Docker', license: 'MIT', url: 'https://github.com/testcontainers/Docker.DotNet' },
  { name: 'MailKit', what: 'Email notifications', license: 'MIT', url: 'https://github.com/jstedfast/MailKit' },
  { name: 'Inter', what: 'Typeface', license: 'OFL-1.1', url: 'https://rsms.me/inter/' },
]

const fetchText = (path: string) => () => fetch(path).then((r) => (r.ok ? r.text() : Promise.reject(new Error(`${r.status}`))))

export function About() {
  const { data } = useSystemInfo()

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <Card className="flex flex-wrap items-center gap-6 p-8">
        <img src={logoMark} alt="" className="size-20" />
        <div className="min-w-0 flex-1">
          <h2 className="text-2xl font-semibold tracking-tight">Stowaway</h2>
          <p className="text-muted">Sensible Docker backup management</p>
          <p className="mt-3 text-sm">
            Version {data ? displayVersion(data.version) : '…'}
            {data && <span className="text-muted"> · {data.runningAsService ? 'Windows service' : 'console mode'} on {data.machineName}</span>}
          </p>
          <p className="mt-1 text-sm text-muted">
            {copyright}. Free and open source under the MIT License.
          </p>
        </div>
      </Card>

      <div className="grid gap-3 sm:grid-cols-2">
        <LinkCard href={repositoryUrl} icon={<CodeXml className="size-4" />} title="Source code" text="github.com/niogtres/stowaway" />
        <LinkCard href={`${repositoryUrl}/issues`} icon={<Bug className="size-4" />} title="Report a problem or suggest a feature" text="GitHub issues" />
        <LinkCard href={`${repositoryUrl}/security/policy`} icon={<ShieldCheck className="size-4" />} title="Report a security issue" text="Privately, through GitHub" />
        <Link to="/help" className="glass flex items-center gap-3 rounded-2xl p-4 transition hover:bg-accent-soft">
          <BookOpen className="size-4 text-accent" />
          <span>
            <span className="block text-sm font-medium">Help</span>
            <span className="block text-xs text-muted">The user guide, built in</span>
          </span>
        </Link>
      </div>

      <Card>
        <h3 className="mb-3 text-sm font-medium">Built with</h3>
        <ul className="divide-y divide-line text-sm">
          {builtWith.map((b) => (
            <li key={b.name} className="flex flex-wrap items-baseline justify-between gap-x-4 py-2">
              <a href={b.url} target="_blank" rel="noopener noreferrer" className="font-medium hover:text-accent">
                {b.name}
              </a>
              <span className="flex-1 text-muted">{b.what}</span>
              <span className="font-mono text-xs text-muted">{b.license}</span>
            </li>
          ))}
        </ul>
        <p className="mt-3 text-xs text-muted">restic and rclone run inside Docker containers on your computer and aren't part of the installer.</p>
      </Card>

      <TextPanel title="License" path="/api/about/license" />
      <TextPanel title="Third-party notices" path="/api/about/notices" hint="Every package the installer ships, with its license." />
    </div>
  )
}

function LinkCard({ href, icon, title, text }: { href: string; icon: ReactNode; title: string; text: string }) {
  return (
    <a href={href} target="_blank" rel="noopener noreferrer" className="glass flex items-center gap-3 rounded-2xl p-4 transition hover:bg-accent-soft">
      <span className="text-accent">{icon}</span>
      <span className="min-w-0 flex-1">
        <span className="block text-sm font-medium">{title}</span>
        <span className="block truncate text-xs text-muted">{text}</span>
      </span>
      <ExternalLink className="size-3.5 text-muted" />
    </a>
  )
}

function TextPanel({ title, path, hint }: { title: string; path: string; hint?: string }) {
  const [open, setOpen] = useState(false)
  const text = useQuery({ queryKey: ['about', path], queryFn: fetchText(path), enabled: open, staleTime: Infinity })

  return (
    <Card>
      <div className="flex items-center justify-between gap-4">
        <div>
          <h3 className="text-sm font-medium">{title}</h3>
          {hint && <p className="text-xs text-muted">{hint}</p>}
        </div>
        <Button variant="ghost" className="text-xs" onClick={() => setOpen((o) => !o)} aria-expanded={open}>
          {open ? 'Hide' : 'Show'}
        </Button>
      </div>
      {open && (
        <pre className="mt-3 max-h-96 overflow-auto whitespace-pre-wrap rounded-xl border border-line bg-surface-strong p-4 font-mono text-xs leading-relaxed">
          {text.isPending ? 'Loading…' : text.isError ? 'Not available in this build.' : text.data}
        </pre>
      )}
    </Card>
  )
}
