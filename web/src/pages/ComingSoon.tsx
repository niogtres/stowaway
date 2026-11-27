import type { LucideIcon } from 'lucide-react'
import { motion } from 'motion/react'
import { Badge, Card } from '@/components/ui'

interface ComingSoonProps {
  icon: LucideIcon
  title: string
  description: string
  milestone: string
  /** Optional empty-state illustration (4:3); falls back to the icon. */
  illustration?: string
}

export function ComingSoon({ icon: Icon, title, description, milestone, illustration }: ComingSoonProps) {
  return (
    <motion.div initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.4 }} className="mx-auto max-w-xl pt-10">
      <Card className="flex flex-col items-center gap-4 px-10 pb-12 pt-6 text-center">
        {illustration ? (
          <img src={illustration} alt="" width={320} height={240} className="h-60 w-80 select-none object-contain" draggable={false} />
        ) : (
          <div className="mt-6 grid size-14 place-items-center rounded-2xl bg-accent-soft">
            <Icon className="size-7 text-accent" />
          </div>
        )}
        <div>
          <h2 className="text-lg font-semibold tracking-tight">{title}</h2>
          <p className="mt-2 text-sm text-muted">{description}</p>
        </div>
        <Badge tone="accent">Coming in {milestone}</Badge>
      </Card>
    </motion.div>
  )
}
