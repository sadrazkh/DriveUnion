<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { bytes as formatBytes } from '../uploads/store';
import { clear, list, remove, room, supported, type SavedFile } from '../offline/library';

/**
 * What this browser is holding, and the one press that empties it.
 *
 * <p>Everything on this screen is read from the device rather than from the server, which is the
 * whole reason it is an island over an otherwise empty Razor page: the server has no idea what is
 * kept here and must not — it is the browser's own storage, per device and per browser, and a list
 * the server could produce would be a list of somewhere else.</p>
 *
 * <p>That is also the one thing a reader will not guess, so the page says it rather than leaving
 * somebody to wonder why their phone and their laptop disagree.</p>
 */

const props = defineProps<{
  lang: string;
  text: {
    hint: string;
    empty: string;
    clearAll: string;
    remove: string;
    watch: string;
    usage: string;
    cannot: string;
  };
}>();

const saved = ref<SavedFile[]>([]);
const free = ref(0);
const loaded = ref(false);

const total = computed(() => saved.value.reduce((sum, e) => sum + e.bytes, 0));

const fa = computed(() => props.lang !== 'en');

async function refresh() {
  saved.value = await list();
  free.value = (await room()).free;
  loaded.value = true;
}

onMounted(refresh);

async function forget(key: string) {
  await remove(key);
  await refresh();
}

async function forgetEverything() {
  await clear();
  await refresh();
}

/**
 * The date a copy was taken, in the reader's own locale.
 *
 * <p>`savedAt` is epoch milliseconds written by whichever page did the saving — the library holds no
 * clock, so this is the only place a date is built.</p>
 */
function when(at: number): string {
  return new Date(at).toLocaleDateString(fa.value ? 'fa-IR' : 'en-GB', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}
</script>

<template>
  <div class="stack" style="gap: 14px;">
    <p class="muted" style="font-size: 12.5px; line-height: 1.9; margin: 0;">{{ props.text.hint }}</p>

    <p v-if="!supported()" class="muted" style="font-size: 12.5px; margin: 0;">
      {{ props.text.cannot }}
    </p>

    <template v-else-if="loaded">
      <div v-if="saved.length" class="offline-total">
        <span>
          <!-- dir="ltr" on every figure: these are Latin numerals inside a Persian sentence. -->
          <span class="mono" dir="ltr">{{ formatBytes(total) }}</span>
          {{ props.text.usage }}
        </span>

        <button type="button" class="btn btn--sm btn--danger push-end" @click="forgetEverything()">
          {{ props.text.clearAll }}
        </button>
      </div>

      <p v-else class="muted" style="font-size: 12.5px; margin: 0;">{{ props.text.empty }}</p>

      <ul v-if="saved.length" class="offline-list">
        <li v-for="entry in saved" :key="entry.key" class="offline-row">
          <div class="offline-head">
            <!-- dir="auto": the name is whatever the file's owner called it, in whichever script. -->
            <span class="offline-name" dir="auto" :title="entry.name">{{ entry.name }}</span>
          </div>

          <div class="offline-meta">
            <span class="mono" dir="ltr">{{ formatBytes(entry.bytes) }}</span>
            <span>{{ when(entry.savedAt) }}</span>

            <span class="push-end offline-actions">
              <a class="btn btn--sm" :href="entry.watchUrl">{{ props.text.watch }}</a>
              <button type="button" class="btn btn--sm btn--danger" @click="forget(entry.key)">
                {{ props.text.remove }}
              </button>
            </span>
          </div>
        </li>
      </ul>
    </template>
  </div>
</template>

<style scoped>
.offline-total {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px 14px;
  padding: 9px 12px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--surface2);
  font-size: 12px;
}

.offline-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.offline-row {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 12px;
  border: 1px solid var(--line);
  border-radius: 10px;
}

.offline-head {
  display: flex;
  gap: 10px;
  align-items: center;
}

.offline-name {
  font-size: 12.5px;
  font-weight: 600;

  /* A file name is whatever its owner called it, and some have no break opportunity at all. */
  overflow-wrap: anywhere;
}

.offline-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 12px;
  font-size: 11px;
  color: var(--muted);
}

.offline-actions {
  display: flex;
  gap: 6px;
}
</style>
