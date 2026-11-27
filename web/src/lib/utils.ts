import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

export function formatNumber(value: number) {
  return new Intl.NumberFormat().format(value)
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
