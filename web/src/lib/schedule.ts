import type { RetentionPolicy } from './api'

export type ScheduleKind = 'hourly' | 'every6h' | 'daily' | 'weekly' | 'custom'

export interface ScheduleChoice {
  kind: ScheduleKind
  time: string // HH:MM for daily/weekly
  weekday: number // 0 = Sunday
  cron: string // custom
}

export const weekdays = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']

export const defaultSchedule: ScheduleChoice = { kind: 'daily', time: '02:30', weekday: 0, cron: '30 2 * * *' }

export function toCron(choice: ScheduleChoice): string {
  const [hh, mm] = choice.time.split(':').map((n) => Number.parseInt(n, 10))
  const hour = Number.isFinite(hh) ? hh : 2
  const minute = Number.isFinite(mm) ? mm : 30
  switch (choice.kind) {
    case 'hourly':
      return '0 * * * *'
    case 'every6h':
      return '0 */6 * * *'
    case 'daily':
      return `${minute} ${hour} * * *`
    case 'weekly':
      return `${minute} ${hour} * * ${choice.weekday}`
    default:
      return choice.cron.trim()
  }
}

const pad = (n: number) => n.toString().padStart(2, '0')

/** Recognises the cron strings our presets produce, so editing reopens the right preset. */
export function fromCron(cron: string): ScheduleChoice {
  const c = cron.trim()
  if (c === '0 * * * *') return { ...defaultSchedule, kind: 'hourly', cron: c }
  if (c === '0 */6 * * *') return { ...defaultSchedule, kind: 'every6h', cron: c }
  const daily = /^(\d{1,2}) (\d{1,2}) \* \* \*$/.exec(c)
  if (daily) return { ...defaultSchedule, kind: 'daily', time: `${pad(+daily[2])}:${pad(+daily[1])}`, cron: c }
  const weekly = /^(\d{1,2}) (\d{1,2}) \* \* ([0-6])$/.exec(c)
  if (weekly) return { ...defaultSchedule, kind: 'weekly', time: `${pad(+weekly[2])}:${pad(+weekly[1])}`, weekday: +weekly[3], cron: c }
  return { ...defaultSchedule, kind: 'custom', cron: c }
}

export function describeSchedule(cron: string): string {
  const choice = fromCron(cron)
  switch (choice.kind) {
    case 'hourly':
      return 'Every hour'
    case 'every6h':
      return 'Every 6 hours'
    case 'daily':
      return `Daily at ${choice.time}`
    case 'weekly':
      return `${weekdays[choice.weekday]}s at ${choice.time}`
    default:
      return `Custom (${cron})`
  }
}

export type RetentionPreset = 'standard' | 'light' | 'longTerm' | 'custom'

export const retentionPresets: Record<Exclude<RetentionPreset, 'custom'>, { label: string; text: string; policy: RetentionPolicy }> = {
  standard: {
    label: 'Standard',
    text: '7 daily, 4 weekly, 12 monthly',
    policy: { keepLast: 3, keepHourly: 0, keepDaily: 7, keepWeekly: 4, keepMonthly: 12, keepYearly: 0 },
  },
  light: {
    label: 'Light',
    text: 'The latest 7 only',
    policy: { keepLast: 7, keepHourly: 0, keepDaily: 0, keepWeekly: 0, keepMonthly: 0, keepYearly: 0 },
  },
  longTerm: {
    label: 'Long-term',
    text: '14 daily, 8 weekly, 24 monthly, 5 yearly',
    policy: { keepLast: 3, keepHourly: 0, keepDaily: 14, keepWeekly: 8, keepMonthly: 24, keepYearly: 5 },
  },
}

export function presetFor(policy: RetentionPolicy): RetentionPreset {
  for (const [key, preset] of Object.entries(retentionPresets)) {
    if (Object.entries(preset.policy).every(([k, v]) => policy[k as keyof RetentionPolicy] === v)) return key as RetentionPreset
  }
  return 'custom'
}

export function describeRetention(policy: RetentionPolicy): string {
  const parts: string[] = []
  const add = (n: number, word: string) => n > 0 && parts.push(`${n} ${word}`)
  add(Math.max(1, policy.keepLast), 'latest')
  add(policy.keepHourly, 'hourly')
  add(policy.keepDaily, 'daily')
  add(policy.keepWeekly, 'weekly')
  add(policy.keepMonthly, 'monthly')
  add(policy.keepYearly, 'yearly')
  return `Keeps ${parts.join(', ')}`
}
