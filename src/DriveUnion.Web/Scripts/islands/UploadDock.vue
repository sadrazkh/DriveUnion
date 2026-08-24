<script setup lang="ts">
import { computed, ref } from 'vue';
import {
  bytes,
  percentOf,
  sent,
  type UploadConfig,
  type UploadItem,
  type UploadStore,
} from '../uploads/store';

/**
 * The dock: what is uploading, in the corner of every page, whatever page that is.
 *
 * It is a view onto the shared queue and owns nothing. The queue lives above the content that
 * navigation swaps (see uploads/store.ts), so this island is mounted once in the shell and is never
 * torn down — which is the only reason a transfer survives walking around the panel.
 *
 * Collapsed it is one line, because that is what a corner is worth: what is happening, how many
 * files it is happening to, how far along the whole thing is, and a way in. Expanded it is the same
 * list the upload screen draws, minus everything that needs room.
 *
 * It draws nothing at all when there is nothing in the queue. A permanent empty box in the corner of
 * every screen is furniture, and the store already knows when there is something to say.
 */

const props = defineProps<{
  store: UploadStore;
  /**
   * The same reader the store takes, and read for the same reason: `data-upload-config` is
   * re-rendered by every response, so its `data-lang` is the culture of the page on the screen now
   * rather than the one this island happened to mount under.
   */
  config: () => UploadConfig;
}>();

const { items, busy, totalPercent, inFlightItems, pause, resume, cancel, retry, clearFinished } =
  props.store;

const open = ref(false);

/**
 * A cancelled row is not listed and does not hold the dock open — cancelling is how you dismiss one.
 * A finished or failed row is listed: «Clear finished» is the way out of the first, and the second
 * is the whole reason a dock is better than a toast that has already gone.
 */
const listed = computed(() => items.value.filter((i) => i.status !== 'cancelled'));

const shown = computed(() => listed.value.length > 0);

/**
 * The words, rebuilt per render rather than held in a computed.
 *
 * A computed over `props.config()` reads the DOM once and then caches for ever: it has no reactive
 * dependency to invalidate it, so a swap that brought a differently-lettered page would leave this
 * island speaking the previous one's language. Rebuilding a small object each render costs nothing
 * next to the progress bars this component is already repainting.
 */
function text() {
  return props.config().lang === 'en'
    ? {
        dock: 'Uploads',
        uploading: 'Uploading',
        paused: 'Paused',
        failed: 'Failed',
        finished: 'Finished',
        queued: 'Queued',
        done: 'Done',
        cancelled: 'Cancelled',
        openScreen: 'Open the upload screen',
        expand: 'Show the files',
        collapse: 'Hide the files',
        pause: 'Pause',
        resume: 'Resume',
        cancel: 'Cancel',
        retry: 'Try again',
        clear: 'Clear finished',
      }
    : {
        dock: 'آپلودها',
        uploading: 'در حال آپلود',
        paused: 'متوقف',
        failed: 'ناموفق',
        finished: 'تمام شد',
        queued: 'در صف',
        done: 'انجام شد',
        cancelled: 'لغو شد',
        openScreen: 'رفتن به صفحه‌ی آپلود',
        expand: 'دیدن فایل‌ها',
        collapse: 'بستن فهرست',
        pause: 'توقف',
        resume: 'ادامه',
        cancel: 'لغو',
        retry: 'تلاش دوباره',
        clear: 'پاک کردن تمام‌شده‌ها',
      };
}

const statusWord = (item: UploadItem) => text()[item.status];

/**
 * One line: what the queue is doing, and how many files it is doing it to.
 *
 * The count follows the word rather than counting everything in the dock, because «paused» beside
 * the number of files that are not paused is a sentence that argues with itself.
 */
function summary(): { label: string; count: number } {
  const t = text();

  if (busy.value) return { label: t.uploading, count: inFlightItems.value.length };

  const paused = items.value.filter((i) => i.status === 'paused').length;
  if (paused > 0) return { label: t.paused, count: paused };

  const failed = items.value.filter((i) => i.status === 'failed').length;
  if (failed > 0) return { label: t.failed, count: failed };

  return { label: t.finished, count: items.value.filter((i) => i.status === 'done').length };
}

const anyFinished = computed(() =>
  items.value.some((i) => i.status === 'done' || i.status === 'cancelled'));
</script>

<template>
  <!--
    Not drawn at all when there is nothing queued, so the corner belongs to the page again the
    moment the last transfer is cleared.
  -->
  <section v-if="shown" class="upload-dock" :aria-label="text().dock">
    <div class="upload-dock-head">
      <!--
        The line itself is the way in: Drive's dock opens Drive's upload view, and this one opens
        the screen where the queue can be selected, paused in bulk and re-ordered by concurrency.
        The expand control is beside it rather than on it, so one press is «show me» and the other
        is «take me there».
      -->
      <a class="upload-dock-open" href="/Files/Upload" :title="text().openScreen">
        <span class="upload-dock-label">{{ summary().label }}</span>
        <!--
          dir="ltr" on the run itself. In an RTL box the bidi algorithm resolves the space between a
          European number and a Latin unit as right-to-left, so «0 B / 202 MB» is laid out
          «B / 202 MB 0» — the shape the first uploader shipped. The direction has to be stated
          where the figure is, never on an ancestor that also holds a Persian sentence.
        -->
        <span class="mono upload-dock-count" dir="ltr">{{ summary().count }}</span>
        <span class="mono upload-dock-pct" dir="ltr">{{ Math.round(totalPercent) }}%</span>
      </a>

      <button
        type="button"
        class="btn btn--sm upload-dock-toggle"
        :aria-expanded="open"
        :title="open ? text().collapse : text().expand"
        @click="open = !open"
      >
        <span aria-hidden="true">{{ open ? '⌄' : '⌃' }}</span>
        <span class="visually-hidden">{{ open ? text().collapse : text().expand }}</span>
      </button>
    </div>

    <div
      class="bar"
      role="progressbar"
      :aria-valuenow="Math.round(totalPercent)"
      aria-valuemin="0"
      aria-valuemax="100"
    >
      <span class="bar-fill" :style="{ width: `${totalPercent}%` }"></span>
    </div>

    <ul v-if="open" class="upload-dock-list">
      <li v-for="item in listed" :key="item.id" class="upload-dock-row">
        <div class="upload-dock-row-head">
          <!--
            dir="auto" and not ltr: a file name is whatever the person named it, so the only honest
            answer is to let the first strong character decide. A Persian name in an ltr isolate is
            the same defect as a byte figure without one, from the other side.
          -->
          <span class="upload-dock-name" dir="auto" :title="item.file.name">{{ item.file.name }}</span>
          <span class="mono" dir="ltr">{{ Math.round(percentOf(item)) }}%</span>
        </div>

        <div
          class="bar"
          role="progressbar"
          :aria-valuenow="Math.round(percentOf(item))"
          aria-valuemin="0"
          aria-valuemax="100"
        >
          <span class="bar-fill" :style="{ width: `${percentOf(item)}%` }"></span>
        </div>

        <div class="upload-dock-row-foot">
          <span class="upload-dock-state">{{ statusWord(item) }}</span>
          <span class="mono" dir="ltr">{{ bytes(sent(item)) }} / {{ bytes(item.file.size) }}</span>

          <span class="push-end upload-dock-actions">
            <button
              v-if="item.status === 'uploading' || item.status === 'queued'"
              type="button"
              class="btn btn--sm"
              @click="pause(item.id)"
            >{{ text().pause }}</button>

            <button
              v-else-if="item.status === 'paused'"
              type="button"
              class="btn btn--sm"
              @click="resume(item.id)"
            >{{ text().resume }}</button>

            <button
              v-else-if="item.status === 'failed'"
              type="button"
              class="btn btn--sm"
              @click="retry(item.id)"
            >{{ text().retry }}</button>

            <button
              v-if="item.status !== 'done'"
              type="button"
              class="btn btn--sm"
              @click="cancel(item.id)"
            >{{ text().cancel }}</button>
          </span>
        </div>

        <p v-if="item.error" class="upload-dock-error">{{ item.error }}</p>
      </li>
    </ul>

    <!--
      The way the dock ends. It clears what has finished, which is the store's own idea of finished —
      a failure stays, and so does the dock, because a corner that tidies away the one row somebody
      needed to read is worse than a corner that stays.
    -->
    <button
      v-if="open && anyFinished"
      type="button"
      class="btn btn--sm btn--block"
      @click="clearFinished()"
    >{{ text().clear }}</button>
  </section>
</template>

<style scoped>
/* The corner itself — position, width and layering — is in app.css beside the shell's other fixed
   boxes, because where this sits is a decision about the shell rather than about the component. */
.upload-dock {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
}

.upload-dock-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* The row is an anchor, and `a { color: var(--accent) }` would paint the whole summary green and
   underline it on hover. It is a surface to press, not a link in a sentence. */
.upload-dock-open {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: baseline;
  gap: 8px;
  color: var(--text);
  font-size: 12.5px;
}

.upload-dock-open:hover {
  color: var(--accent-ink);
  text-decoration: none;
}

.upload-dock-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.upload-dock-count,
.upload-dock-pct {
  font-size: 11.5px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.upload-dock-pct {
  margin-inline-start: auto;
  color: var(--text);
}

.upload-dock-toggle {
  flex: 0 0 auto;
  padding: 4px 9px;
}

/* Tall enough for four rows; past that the dock scrolls rather than growing up the screen and
   covering the page it is meant to sit beside. */
.upload-dock-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 9px;
  max-height: 46vh;
  overflow-y: auto;
}

.upload-dock-row {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding-top: 8px;
  border-top: 1px solid var(--line);
}

.upload-dock-row-head {
  display: flex;
  align-items: baseline;
  gap: 8px;
  font-size: 12px;
}

.upload-dock-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.upload-dock-row-foot {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px 10px;
  font-size: 11px;
  color: var(--muted);
}

.upload-dock-actions {
  display: flex;
  gap: 6px;
}

.upload-dock-actions .btn {
  padding: 3px 8px;
  font-size: 11px;
}

.upload-dock-error {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.6;
  color: var(--danger);
}
</style>
