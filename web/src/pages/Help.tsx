import { Link, useNavigate } from '@tanstack/react-router'
import { ArrowLeft, ArrowRight, BookOpen, Search } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type MouseEvent } from 'react'
import { TextInput } from '@/components/forms'
import { Card } from '@/components/ui'
import { helpGroups, helpTopic, helpTopics, searchHelp } from '@/lib/help'
import { cn } from '@/lib/utils'

/** Help: the user guide as a small wiki (topics, cross-links, search), built into the app. */
export function Help({ slug, hash }: { slug?: string; hash?: string }) {
  const [query, setQuery] = useState('')
  const hits = useMemo(() => searchHelp(query), [query])
  const topic = slug ? helpTopic(slug) : undefined

  return (
    <div className="mx-auto grid max-w-6xl gap-6 lg:grid-cols-[15rem_1fr]">
      <aside className="space-y-5 lg:sticky lg:top-0 lg:self-start">
        <label className="relative block">
          <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" />
          <TextInput value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search help" className="pl-9" aria-label="Search help" />
        </label>
        <nav aria-label="Help topics" className="space-y-4 text-sm">
          {helpGroups.map((group) => (
            <div key={group.title}>
              <div className="mb-1 px-2 text-[11px] font-medium uppercase tracking-wider text-muted">{group.title}</div>
              {group.slugs.map((s) => {
                const t = helpTopic(s)
                return t ? (
                  <Link
                    key={s}
                    to="/help/$slug"
                    params={{ slug: s }}
                    onClick={() => setQuery('')}
                    className={cn('block rounded-lg px-2 py-1 transition hover:bg-accent-soft', s === slug && !query ? 'bg-accent-soft text-fg' : 'text-muted hover:text-fg')}
                  >
                    {t.title}
                  </Link>
                ) : null
              })}
            </div>
          ))}
          <Link to="/about" className="block rounded-lg px-2 py-1 text-muted transition hover:bg-accent-soft hover:text-fg">
            About Stowaway
          </Link>
        </nav>
      </aside>

      <div className="min-w-0">
        {query.trim() ? (
          <SearchResults query={query} hits={hits} onPick={() => setQuery('')} />
        ) : topic ? (
          <Article key={topic.slug} slug={topic.slug} hash={hash} />
        ) : slug ? (
          <Card className="py-10 text-center text-sm text-muted">There's no help page called “{slug}”.</Card>
        ) : (
          <HelpHome />
        )}
      </div>
    </div>
  )
}

function HelpHome() {
  return (
    <div>
      <div className="mb-6 flex items-center gap-3">
        <div className="grid size-10 place-items-center rounded-xl bg-accent-soft">
          <BookOpen className="size-5 text-accent" />
        </div>
        <div>
          <h2 className="text-lg font-semibold tracking-tight">Stowaway help</h2>
          <p className="text-sm text-muted">Everything from the first backup to recovering a whole app. Search, or pick a topic.</p>
        </div>
      </div>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
        {helpGroups.map((group) => (
          <Card key={group.title} className="space-y-2">
            <h3 className="text-sm font-medium">{group.title}</h3>
            <ul className="space-y-1 text-sm">
              {group.slugs.map((s) => (
                <li key={s}>
                  <Link to="/help/$slug" params={{ slug: s }} className="text-accent hover:underline">
                    {helpTopic(s)?.title}
                  </Link>
                </li>
              ))}
            </ul>
          </Card>
        ))}
      </div>
    </div>
  )
}

function SearchResults({ query, hits, onPick }: { query: string; hits: ReturnType<typeof searchHelp>; onPick: () => void }) {
  if (hits.length === 0) {
    return <Card className="py-10 text-center text-sm text-muted">Nothing found for “{query.trim()}”. Try fewer or different words.</Card>
  }
  return (
    <div className="space-y-2">
      <p className="text-sm text-muted">
        {hits.length} result{hits.length === 1 ? '' : 's'} for “{query.trim()}”
      </p>
      {hits.map((hit) => (
        <Link
          key={`${hit.topic.slug}#${hit.section.id}`}
          to="/help/$slug"
          params={{ slug: hit.topic.slug }}
          hash={hit.section.id || undefined}
          onClick={onPick}
          className="glass block rounded-2xl p-4 transition hover:bg-accent-soft"
        >
          <div className="text-sm font-medium">
            {hit.topic.title}
            {hit.section.id && <span className="text-muted"> › {hit.section.heading}</span>}
          </div>
          <p className="mt-1 text-xs text-muted">{hit.snippet}</p>
        </Link>
      ))}
    </div>
  )
}

function Article({ slug, hash }: { slug: string; hash?: string }) {
  const topic = helpTopic(slug)!
  const navigate = useNavigate()
  const ref = useRef<HTMLElement>(null)
  const index = helpTopics.indexOf(topic)
  const previous = helpTopics[index - 1]
  const next = helpTopics[index + 1]

  useEffect(() => {
    const target = hash ? ref.current?.querySelector(`#${CSS.escape(hash)}`) : null
    if (target) target.scrollIntoView({ block: 'start' })
    else ref.current?.closest('main')?.scrollTo({ top: 0 })
  }, [slug, hash])

  // Links between help pages navigate inside the app instead of reloading it.
  const onClick = (e: MouseEvent) => {
    const a = (e.target as HTMLElement).closest('a[data-help-link]')
    if (!a || e.ctrlKey || e.metaKey || e.shiftKey) return
    e.preventDefault()
    const url = new URL(a.getAttribute('href')!, window.location.origin + `/help/${slug}`)
    const target = url.pathname.split('/').pop()!
    void navigate({ to: '/help/$slug', params: { slug: target }, hash: url.hash.slice(1) || undefined })
  }

  return (
    <article>
      {/* Our own Markdown, bundled at build time (docs/help); not user input. */}
      <section ref={ref} onClick={onClick} className="help-prose glass rounded-2xl p-6 lg:p-8" dangerouslySetInnerHTML={{ __html: topic.html }} />
      <nav className="mt-4 flex justify-between gap-4 text-sm" aria-label="More help">
        {previous ? (
          <Link to="/help/$slug" params={{ slug: previous.slug }} className="inline-flex items-center gap-1.5 text-muted transition hover:text-fg">
            <ArrowLeft className="size-4" /> {previous.title}
          </Link>
        ) : (
          <span />
        )}
        {next && (
          <Link to="/help/$slug" params={{ slug: next.slug }} className="inline-flex items-center gap-1.5 text-muted transition hover:text-fg">
            {next.title} <ArrowRight className="size-4" />
          </Link>
        )}
      </nav>
    </article>
  )
}
