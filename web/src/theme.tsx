import { useState } from 'react'

/** Auto follows the device's own light or dark setting; the other two are a choice made here and remembered in this browser. */
export type Theme = 'auto' | 'light' | 'dark'

const KEY = 'tideline.theme'

export function storedTheme(): Theme {
  try {
    const value = localStorage.getItem(KEY)
    return value === 'light' || value === 'dark' ? value : 'auto'
  } catch {
    return 'auto' // storage blocked: the page still works, it just does not remember
  }
}

export function applyTheme(theme: Theme) {
  if (theme === 'auto') document.documentElement.removeAttribute('data-theme')
  else document.documentElement.setAttribute('data-theme', theme)
}

function remember(theme: Theme) {
  try {
    if (theme === 'auto') localStorage.removeItem(KEY)
    else localStorage.setItem(KEY, theme)
  } catch {
    // not remembered; nothing else depends on it
  }
}

const CHOICES: { value: Theme; label: string }[] = [
  { value: 'auto', label: 'Auto' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
]

export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(storedTheme)

  const choose = (next: Theme) => {
    setTheme(next)
    applyTheme(next)
    remember(next)
  }

  return (
    <div className="theme-toggle seg" role="group" aria-label="Colour theme">
      {CHOICES.map((c) => (
        <button key={c.value} type="button" className={theme === c.value ? 'on' : ''} aria-pressed={theme === c.value} onClick={() => choose(c.value)}>
          {c.label}
        </button>
      ))}
    </div>
  )
}
