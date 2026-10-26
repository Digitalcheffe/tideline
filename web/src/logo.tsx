/**
 * The Tideline mark: four rounded bars, like the marks a tide leaves on a pier and the ruled lines of a ledger. The shorter amber bar is "now".
 * Teal follows the theme (lighter in dark mode); the amber is the same in both.
 */
export function LogoMark({ size = 28 }: { size?: number }) {
  return (
    <svg viewBox="0 -30 368 369" width={size} height={size} aria-hidden="true" className="logo-mark">
      <rect x="0" y="0" width="368" height="58" rx="29" fill="var(--accent)" />
      <rect x="0" y="84" width="233" height="58" rx="29" fill="#e0a458" />
      <rect x="0" y="168" width="330" height="58" rx="29" fill="var(--accent)" />
      <rect x="0" y="251" width="210" height="58" rx="29" fill="var(--accent)" />
    </svg>
  )
}

/**
 * The name, with a water drop for the dot of the second "i", as in the logo. The "i" is the dotless letter so the drop can sit where its dot
 * would be. Read aloud and found by tests as plain "Tideline".
 */
export function Wordmark() {
  return (
    <span className="wordmark" role="img" aria-label="Tideline">
      <span aria-hidden="true">
        Tidel
        <span className="drop-i">{'\u0131'}</span>ne
      </span>
    </span>
  )
}
