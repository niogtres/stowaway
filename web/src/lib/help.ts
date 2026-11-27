import { marked, type Tokens } from 'marked'

// The help pages are the user guide in docs/help/ (one Markdown file per topic, "NN-slug.md"), bundled at build time.
// They read the same on GitHub; links between them ("04-destinations.md") become in-app routes (/help/destinations).
const files = import.meta.glob('../../../docs/help/*.md', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

export interface HelpSection {
  id: string
  heading: string
  text: string
}

export interface HelpTopic {
  slug: string
  order: number
  title: string
  html: string
  /** Plain text per section, for search. */
  sections: HelpSection[]
}

export const helpGroups: { title: string; slugs: string[] }[] = [
  { title: 'Getting started', slugs: ['before-you-start', 'install', 'signing-in'] },
  { title: 'Backing up', slugs: ['destinations', 'plans', 'activity'] },
  { title: 'Restoring', slugs: ['restore', 'recreate', 'drills'] },
  { title: 'Staying informed', slugs: ['notifications', 'tray'] },
  { title: 'Remote access', slugs: ['remote-access', 'internet-access'] },
  { title: 'When things go wrong', slugs: ['troubleshooting', 'uninstall'] },
]

const fileName = /^(?:\d+-)?([a-z0-9-]+)\.md(#.*)?$/

export const slugify = (text: string) =>
  text
    .toLowerCase()
    .replace(/<[^>]+>/g, '')
    .replace(/[^a-z0-9 -]/g, '')
    .trim()
    .replace(/\s+/g, '-')

const plain = (markdown: string) =>
  markdown
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_>#|]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()

function render(markdown: string): string {
  const renderer = new marked.Renderer()
  renderer.heading = function ({ tokens, depth }: Tokens.Heading) {
    const inner = this.parser.parseInline(tokens)
    return `<h${depth} id="${slugify(inner)}">${inner}</h${depth}>\n`
  }
  renderer.link = function ({ href, title, tokens }: Tokens.Link) {
    const text = this.parser.parseInline(tokens)
    const internal = href.match(fileName)
    if (internal) return `<a href="/help/${internal[1]}${internal[2] ?? ''}" data-help-link>${text}</a>`
    if (href.startsWith('#')) return `<a href="${href}" data-help-link>${text}</a>`
    const t = title ? ` title="${title}"` : ''
    return `<a href="${href}"${t} target="_blank" rel="noopener noreferrer">${text}</a>`
  }
  return marked.parse(markdown, { renderer, async: false, gfm: true })
}

function topic(path: string, markdown: string): HelpTopic {
  const name = path.split('/').pop()!
  const match = name.match(/^(\d+)-([a-z0-9-]+)\.md$/)
  const order = match?.[1] ?? '99'
  const slug = match?.[2] ?? name.replace(/\.md$/, '')
  const title = markdown.match(/^# (.+)$/m)?.[1] ?? slug
  const sections: HelpSection[] = []
  let current: HelpSection = { id: '', heading: title, text: '' }
  for (const line of markdown.split('\n').slice(1)) {
    const h = line.match(/^#{2,3} (.+)$/)
    if (h) {
      sections.push(current)
      current = { id: slugify(h[1]), heading: h[1], text: '' }
    } else {
      current.text += `${line}\n`
    }
  }
  sections.push(current)
  return { slug, order: Number(order), title, html: render(markdown), sections: sections.map((s) => ({ ...s, text: plain(s.text) })).filter((s) => s.text) }
}

export const helpTopics: HelpTopic[] = Object.entries(files)
  .map(([path, markdown]) => topic(path, markdown))
  .sort((a, b) => a.order - b.order)

export const helpTopic = (slug: string) => helpTopics.find((t) => t.slug === slug)

export interface HelpHit {
  topic: HelpTopic
  section: HelpSection
  snippet: string
}

/** Every word must appear in the section (or its topic or heading); best matches first. */
export function searchHelp(query: string, limit = 20): HelpHit[] {
  const words = query.toLowerCase().split(/\s+/).filter((w) => w.length > 1)
  if (words.length === 0) return []
  const hits: (HelpHit & { score: number })[] = []
  for (const topic of helpTopics) {
    for (const section of topic.sections) {
      const haystack = `${topic.title} ${section.heading} ${section.text}`.toLowerCase()
      if (!words.every((w) => haystack.includes(w))) continue
      const score = words.reduce(
        (sum, w) => sum + (topic.title.toLowerCase().includes(w) ? 3 : 0) + (section.heading.toLowerCase().includes(w) ? 2 : 0) + 1,
        0,
      )
      const at = Math.max(0, section.text.toLowerCase().indexOf(words[0]) - 60)
      const snippet = (at > 0 ? '…' : '') + section.text.slice(at, at + 180) + (section.text.length > at + 180 ? '…' : '')
      hits.push({ topic, section, snippet, score })
    }
  }
  return hits.sort((a, b) => b.score - a.score).slice(0, limit)
}

/** The help topic for each page, for the "?" button in the header. */
export const helpForPath = (pathname: string) =>
  pathname.startsWith('/snapshots')
    ? 'restore'
    : pathname.startsWith('/activity')
      ? 'activity'
      : pathname.startsWith('/destinations')
        ? 'destinations'
        : pathname.startsWith('/settings')
          ? 'notifications'
          : pathname.startsWith('/plans') || pathname.startsWith('/inventory') || pathname === '/'
            ? 'plans'
            : null
