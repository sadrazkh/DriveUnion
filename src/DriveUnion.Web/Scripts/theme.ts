export type Theme = 'light' | 'dark';

/**
 * The one key the theme is stored under.
 *
 * _Layout.cshtml repeats this string literally in the inline script that runs before the
 * stylesheet — it has to, because that script executes before any bundle exists. If you rename it
 * here, rename it there in the same commit: the two drifting apart does not break anything
 * loudly, it just makes the panel flash the wrong theme on every first paint.
 */
export const THEME_KEY = 'driveunion-theme';

/** Reads the persisted choice, falling back to the OS preference exactly as the shell does. */
export function readTheme(): Theme {
  const stored = safeRead();
  if (stored === 'light' || stored === 'dark') return stored;
  return matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

/** True once the visitor has chosen a theme here. An explicit choice outranks the OS preference. */
export function hasStoredTheme(): boolean {
  const stored = safeRead();
  return stored === 'light' || stored === 'dark';
}

/** Paints the theme. The root attribute is what every token in tokens.css keys off. */
export function applyTheme(theme: Theme): void {
  document.documentElement.dataset.theme = theme;
}

export function storeTheme(theme: Theme): void {
  try {
    localStorage.setItem(THEME_KEY, theme);
  } catch {
    // Private mode, or storage full. The theme still applies for this page — losing the
    // preference is not worth an exception that stops the rest of the island from mounting.
  }
}

function safeRead(): string | null {
  try {
    return localStorage.getItem(THEME_KEY);
  } catch {
    return null;
  }
}
