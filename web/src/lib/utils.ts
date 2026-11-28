import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

export function formatNumber(value: number) {
  return new Intl.NumberFormat().format(value)
}

const byteUnits = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']

/** Binary-scaled sizes with SI-style labels, matching what Explorer shows. */
export function formatBytes(bytes: number | null | undefined, { lowerBound = false } = {}) {
  if (bytes === null || bytes === undefined) return '—'
  if (lowerBound && bytes === 0) return 'size unknown'
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < byteUnits.length - 1) {
    value /= 1024
    unit++
  }
  const digits = unit === 0 || value >= 100 ? 0 : value >= 10 ? 1 : 2
  return `${lowerBound ? '≥ ' : ''}${value.toFixed(digits)} ${byteUnits[unit]}`
}

export function formatDuration(fromIso: string, now = Date.now()) {
  const seconds = Math.max(0, Math.floor((now - new Date(fromIso).getTime()) / 1000))
  const units: [number, string][] = [
    [86400, 'd'],
    [3600, 'h'],
    [60, 'm'],
  ]
  for (const [size, label] of units) {
    if (seconds >= size) return `${Math.floor(seconds / size)}${label}`
  }
  return `${seconds}s`
}

export function timeAgo(iso: string, now = Date.now()) {
  const seconds = Math.floor((now - new Date(iso).getTime()) / 1000)
  if (seconds < 10) return 'just now'
  return `${formatDuration(iso, now)} ago`
}

/** Shortens 64-hex anonymous volume names for display. */
export function shortVolumeName(name: string, anonymous: boolean) {
  return anonymous ? name.slice(0, 12) : name
}

/** Relative time for past or future instants. */
export function relativeTime(iso: string | null, now = Date.now()) {
  if (!iso) return '—'
  const diff = new Date(iso).getTime() - now
  return diff > 0 ? `in ${formatDuration(new Date(now - diff).toISOString(), now)}` : timeAgo(iso, now)
}
