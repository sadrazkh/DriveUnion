<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { applyTheme, hasStoredTheme, readTheme, storeTheme, type Theme } from '../theme';

const props = withDefaults(defineProps<{
  /** The language the server rendered this page in. Not a client-side display switch — see below. */
  lang?: 'fa' | 'en';
  /** The public download card shows FA/EN; the panel header shows the theme button alone. */
  showLanguage?: boolean;
}>(), {
  lang: 'fa',
  showLanguage: false,
});

const theme = ref<Theme>('light');
const media = matchMedia('(prefers-color-scheme: dark)');

const isDark = computed(() => theme.value === 'dark');
const otherLang = computed(() => (props.lang === 'fa' ? 'en' : 'fa'));

const themeLabel = computed(() =>
  props.lang === 'fa'
    ? (isDark.value ? 'روشن' : 'تیره')
    : (isDark.value ? 'Light' : 'Dark'));

const languageLabel = computed(() => (props.lang === 'fa' ? 'English' : 'فارسی'));

/**
 * The language switch is a real navigation, not a class flip.
 *
 * The prototype hides `[data-t="en"]` with CSS and ships both languages in every response. M1 §7
 * decided the other way: the server picks FA or EN from `Accept-Language` / `?lang=` before the
 * HTML leaves the box, so the page is cacheable, indexable, and readable with JavaScript off.
 * This control therefore only has to ask for the other rendering.
 */
const otherLangHref = computed(() => {
  const url = new URL(window.location.href);
  url.searchParams.set('lang', otherLang.value);
  return url.pathname + url.search + url.hash;
});

function toggleTheme(): void {
  theme.value = isDark.value ? 'light' : 'dark';
  applyTheme(theme.value);
  storeTheme(theme.value);
}

/** Only while nothing has been chosen here — an explicit choice outranks the OS. */
function followSystem(event: MediaQueryListEvent): void {
  if (hasStoredTheme()) return;
  theme.value = event.matches ? 'dark' : 'light';
  applyTheme(theme.value);
}

onMounted(() => {
  // Read back rather than assume: the inline script in _Layout has already applied a theme before
  // this bundle existed, and disagreeing with it would repaint the page under the visitor.
  theme.value = readTheme();
  applyTheme(theme.value);
  media.addEventListener('change', followSystem);
});

onUnmounted(() => media.removeEventListener('change', followSystem));
</script>

<template>
  <div class="toggle-group">
    <a
      v-if="showLanguage"
      class="btn btn--sm"
      :href="otherLangHref"
      :hreflang="otherLang"
      :lang="otherLang"
    >{{ languageLabel }}</a>

    <button
      type="button"
      class="btn btn--sm toggle-theme"
      :aria-pressed="isDark"
      :title="themeLabel"
      @click="toggleTheme"
    >
      <span class="toggle-glyph" aria-hidden="true">◑</span>
      <span class="visually-hidden">{{ themeLabel }}</span>
    </button>
  </div>
</template>

<style scoped>
.toggle-group {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* The comp's ◑ is a glyph, not an icon font — it inherits colour and needs no asset. Nudged to
   the text baseline because the circle's optical centre sits high in most families. */
.toggle-glyph {
  font-size: 13px;
  line-height: 1;
  transform: translateY(1px);
}
</style>
