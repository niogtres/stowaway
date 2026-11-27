import { Moon, Sun } from 'lucide-react'
import { useEffect, useState } from 'react'

const storageKey = 'dockerbackup.theme'
type Theme = 'dark' | 'light'

function initialTheme(): Theme {
  const stored = localStorage.getItem(storageKey)
  return stored === 'light' || stored === 'dark' ? stored : 'dark'
}

export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(initialTheme)

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark')
    localStorage.setItem(storageKey, theme)
  }, [theme])

  const next = theme === 'dark' ? 'light' : 'dark'
  return (
    <button
      type="button"
      onClick={() => setTheme(next)}
      className="glass grid size-8 place-items-center rounded-full text-muted transition hover:text-fg"
      aria-label={`Switch to ${next} theme`}
      title={`Switch to ${next} theme`}
    >
      {theme === 'dark' ? <Sun className="size-4" /> : <Moon className="size-4" />}
    </button>
  )
}
