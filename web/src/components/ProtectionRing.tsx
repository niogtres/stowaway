import { motion } from 'motion/react'

/**
 * Share of discovered data covered by a healthy plan. `score` is 0–100, or null when there is
 * nothing to measure yet (before discovery has run).
 */
export function ProtectionRing({ score, size = 176 }: { score: number | null; size?: number }) {
  const stroke = 12
  const radius = (size - stroke) / 2
  const circumference = 2 * Math.PI * radius
  const value = score ?? 0
  const color = score === null ? 'var(--muted)' : value >= 90 ? 'var(--ok)' : value >= 60 ? 'var(--warn)' : 'var(--danger)'

  return (
    <div className="relative grid place-items-center" style={{ width: size, height: size }}>
      <svg width={size} height={size} className="-rotate-90" role="img" aria-label={score === null ? 'Protection score not available yet' : `Protection score ${value}%`}>
        <circle cx={size / 2} cy={size / 2} r={radius} fill="none" stroke="var(--line)" strokeWidth={stroke} />
        <motion.circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke={color}
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={circumference}
          initial={{ strokeDashoffset: circumference }}
          animate={{ strokeDashoffset: circumference * (1 - value / 100) }}
          transition={{ duration: 1.2, ease: [0.22, 1, 0.36, 1] }}
        />
      </svg>
      <div className="absolute text-center">
        <div className="text-4xl font-semibold tabular-nums tracking-tight">{score === null ? '—' : `${value}%`}</div>
        <div className="text-xs text-muted">protected</div>
      </div>
    </div>
  )
}
