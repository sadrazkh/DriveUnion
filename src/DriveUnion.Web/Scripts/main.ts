import { createApp } from 'vue';
import ThemeLanguageToggle from './islands/ThemeLanguageToggle.vue';
import { mountNavToggle } from './nav';

/**
 * The one entry Vite compiles. Everything the panel loads on every page hangs off this file.
 *
 * "Islands", not an SPA: Razor renders the page and we hydrate only the interactive nodes. A mount
 * point is any element carrying `data-island="<name>"`; its `data-*` attributes are the props. By
 * attribute rather than by id because a page can have more than one of the same island — the
 * theme control appears in the panel header and again on the public download card.
 */
type IslandMounter = (el: HTMLElement) => void;

const islands: Record<string, IslandMounter> = {
  'theme-language': (el) => {
    createApp(ThemeLanguageToggle, {
      lang: el.dataset.lang === 'en' ? 'en' : 'fa',
      showLanguage: el.dataset.showLanguage === 'true',
    }).mount(el);
  },
};

for (const [name, mount] of Object.entries(islands)) {
  document.querySelectorAll<HTMLElement>(`[data-island="${name}"]`).forEach(mount);
}

mountNavToggle();
