<script setup lang="ts">
import { computed, ref } from 'vue';
import {
  ConcurrencyChoices,
  bytes,
  duration,
  percentOf,
  sent,
  type UploadConfig,
  type UploadItem,
  type UploadStore,
} from '../uploads/store';

/**
 * The upload screen: the second view onto the shared queue, and the one with room.
 *
 * This file used to own the queue — it held the items, ran the pump and sent the chunks — and that
 * is exactly why an upload ended when somebody clicked «فایل‌ها». Everything it used to do now
 * lives in uploads/store.ts, above the content that navigation swaps, and this is a view.
 *
 * That inversion is what the screen is now for. Anything already moving when it is opened is
 * already in this list, because the list is the queue rather than a copy of one; and the controls
 * that need a page rather than a corner — how many files go at once, which files an action applies
 * to — are here rather than in the dock.
 */

const props = defineProps<{
  store: UploadStore;
  /** The reader the store takes. See UploadDock.vue: the config element is re-rendered per response. */
  config: () => UploadConfig;
}>();

const {
  items,
  busy,
  concurrency,
  selected,
  add,
  pause,
  resume,
  cancel,
  remove,
  retry,
  clearFinished,
  setConcurrency,
  pauseSelected,
  resumeSelected,
  cancelSelected,
} = props.store;

const dragging = ref(false);
const input = ref<HTMLInputElement | null>(null);

/**
 * The store's own list, spread into a plain array for `v-for`.
 *
 * Not a second list of numbers: how many files may move at once is the store's decision to hold,
 * because it is the store that honours it. A copy here would be a preference the pump had never
 * heard of.
 */
const choices = [...ConcurrencyChoices];

const anyDone = computed(() => items.value.some((i) => i.status === 'done'));

const anyFinished = computed(() =>
  items.value.some((i) => i.status === 'done' || i.status === 'cancelled'));

const allSelected = computed(() =>
  items.value.length > 0 && items.value.every((i) => i.selected));

/** Rebuilt per render, for the reason UploadDock.vue gives. */
function text() {
  return props.config().lang === 'en'
    ? {
        drop: 'Drop files here',
        or: 'or',
        choose: 'Choose files',
        hint: 'Any number of files, any size. Each one is sent in pieces, so an interruption does not cost the whole transfer.',
        tabWarning:
          'An upload keeps going while this tab stays open, so you can move around the panel — but closing or reloading it stops the transfer, because the browser hands the file back the moment this page goes.',
        atOnce: 'Files at once',
        atOnceHint:
          'How many files move together, the way a download manager asks. The pieces of one file cannot: storage acknowledges a single run of bytes, so a second sender into one file would have nothing to send.',
        selectAll: 'Select all',
        selectedCount: 'selected',
        queued: 'Queued',
        uploading: 'Uploading',
        paused: 'Paused',
        done: 'Done',
        failed: 'Failed',
        cancelled: 'Cancelled',
        pause: 'Pause',
        resume: 'Resume',
        cancel: 'Cancel',
        remove: 'Remove',
        retry: 'Try again',
        clear: 'Clear finished',
        backToFiles: 'Go to files',
        remaining: 'left',
        select: 'Select this file',
      }
    : {
        drop: 'فایل‌ها را این‌جا رها کنید',
        or: 'یا',
        choose: 'انتخاب فایل',
        hint: 'هر تعداد فایل، با هر حجم. هر فایل تکه‌تکه فرستاده می‌شود، پس قطع شدن وسط کار همه‌اش را هدر نمی‌دهد.',
        tabWarning:
          'تا وقتی این تب باز است آپلود ادامه دارد و می‌توانید در پنل بچرخید، اما بستن یا تازه کردن صفحه آپلود را متوقف می‌کند، چون فایل فقط تا وقتی این صفحه زنده است در دست مرورگر می‌ماند.',
        atOnce: 'فایل هم‌زمان',
        atOnceHint:
          'چند فایل با هم بروند — همان انتخابی که در دانلود منیجر هست. تکه‌های یک فایل با هم نمی‌روند: فضای ذخیره‌سازی فقط یک رشته‌ی پیوسته از بایت‌ها را تأیید می‌کند.',
        selectAll: 'انتخاب همه',
        selectedCount: 'انتخاب‌شده',
        queued: 'در صف',
        uploading: 'در حال آپلود',
        paused: 'متوقف',
        done: 'انجام شد',
        failed: 'ناموفق',
        cancelled: 'لغو شد',
        pause: 'توقف',
        resume: 'ادامه',
        cancel: 'لغو',
        remove: 'حذف',
        retry: 'تلاش دوباره',
        clear: 'پاک کردن تمام‌شده‌ها',
        backToFiles: 'رفتن به فایل‌ها',
        remaining: 'باقی‌مانده',
        select: 'انتخاب این فایل',
      };
}

const statusWord = (item: UploadItem) => text()[item.status];

const eta = (item: UploadItem) =>
  item.bytesPerSecond > 0 ? duration((item.file.size - sent(item)) / item.bytesPerSecond) : '';

function onDrop(event: DragEvent) {
  dragging.value = false;
  add(event.dataTransfer?.files ?? null);
}

function onPicked(event: Event) {
  const el = event.target as HTMLInputElement;
  add(el.files);
  // Cleared so choosing the same file twice in a row still raises `change`.
  el.value = '';
}

/** All or none. The mixed state is what the box reports, not a third thing it can be set to. */
function toggleAll() {
  const next = !allSelected.value;
  for (const item of items.value) item.selected = next;
}
</script>

<template>
  <div class="uploader">
    <div
      class="dropzone"
      :class="{ 'dropzone--over': dragging }"
      @dragenter.prevent="dragging = true"
      @dragover.prevent="dragging = true"
      @dragleave.prevent="dragging = false"
      @drop.prevent="onDrop"
    >
      <p class="dropzone-title">{{ text().drop }}</p>
      <p class="dropzone-or">{{ text().or }}</p>
      <button type="button" class="btn btn--primary" @click="input?.click()">
        {{ text().choose }}
      </button>
      <p class="dropzone-hint">{{ text().hint }}</p>
      <input ref="input" type="file" multiple hidden @change="onPicked" />
    </div>

    <!--
      Said here, before the first file is chosen, because the alternative is finding it out with
      90 GB sent. The queue outlives every navigation inside the panel and outlives nothing else:
      a File handle belongs to the page that opened it, and closing or reloading the tab takes it
      back. No worker can rescue one either — it would have to copy the bytes first.
    -->
    <p class="upload-warning">{{ text().tabWarning }}</p>

    <div class="upload-concurrency">
      <span class="field-label">{{ text().atOnce }}</span>
      <div class="seg">
        <button
          v-for="choice in choices"
          :key="choice"
          type="button"
          class="seg-option"
          :class="{ 'is-active': concurrency === choice }"
          :aria-pressed="concurrency === choice"
          @click="setConcurrency(choice)"
        >
          <!-- A figure somebody compares with their line speed, not a number set in prose. -->
          <span class="mono" dir="ltr">{{ choice }}</span>
        </button>
      </div>
      <p class="upload-note">{{ text().atOnceHint }}</p>
    </div>

    <div v-if="items.length" class="upload-bulk">
      <label class="upload-check">
        <input
          type="checkbox"
          :checked="allSelected"
          :indeterminate="selected.length > 0 && !allSelected"
          @change="toggleAll"
        />
        <span>{{ text().selectAll }}</span>
      </label>

      <span class="upload-bulk-count">
        <span class="mono" dir="ltr">{{ selected.length }}</span>
        {{ text().selectedCount }}
      </span>

      <span class="push-end upload-bulk-actions">
        <button
          type="button"
          class="btn btn--sm"
          :disabled="selected.length === 0"
          @click="pauseSelected()"
        >{{ text().pause }}</button>
        <button
          type="button"
          class="btn btn--sm"
          :disabled="selected.length === 0"
          @click="resumeSelected()"
        >{{ text().resume }}</button>
        <button
          type="button"
          class="btn btn--sm btn--danger"
          :disabled="selected.length === 0"
          @click="cancelSelected()"
        >{{ text().cancel }}</button>
        <button
          type="button"
          class="btn btn--sm"
          :disabled="!anyFinished"
          @click="clearFinished()"
        >{{ text().clear }}</button>
      </span>
    </div>

    <ul v-if="items.length" class="upload-list">
      <li
        v-for="item in items"
        :key="item.id"
        class="upload-row"
        :class="`upload-row--${item.status}`"
      >
        <div class="upload-head">
          <label class="upload-check">
            <input type="checkbox" v-model="item.selected" :title="text().select" />
            <span class="visually-hidden">{{ text().select }}</span>
          </label>
          <!-- dir="auto": the name is whatever the person called it. See UploadDock.vue. -->
          <span class="upload-name" dir="auto" :title="item.file.name">{{ item.file.name }}</span>
          <span class="upload-state">{{ statusWord(item) }}</span>
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

        <!--
          dir="ltr" on every run of digits and Latin units, and it is not decoration. In an RTL
          container the browser reorders a bidirectional run by its own rules, so «0 B / 202 MB»
          renders as «0 MB 202 / B» — which is what the owner saw and called unreadable. The
          direction has to be stated where the number is, not on an ancestor: a Persian sentence
          inside one of these isolates is the same defect wearing the other shoe.
        -->
        <div class="upload-meta">
          <span class="upload-percent mono" dir="ltr">{{ Math.round(percentOf(item)) }}%</span>
          <span class="mono" dir="ltr">{{ bytes(sent(item)) }} / {{ bytes(item.file.size) }}</span>
          <span v-if="item.status === 'uploading' && item.bytesPerSecond > 0" class="mono" dir="ltr">
            {{ bytes(item.bytesPerSecond) }}/s
          </span>
          <span v-if="item.status === 'uploading' && eta(item)" class="upload-eta">
            <span class="mono" dir="ltr">{{ eta(item) }}</span> {{ text().remaining }}
          </span>

          <span class="push-end upload-actions">
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
              v-else-if="item.status === 'failed' || item.status === 'cancelled'"
              type="button"
              class="btn btn--sm"
              @click="retry(item.id)"
            >{{ text().retry }}</button>

            <button
              v-if="item.status === 'uploading' || item.status === 'queued' || item.status === 'paused'"
              type="button"
              class="btn btn--sm btn--danger"
              @click="cancel(item.id)"
            >{{ text().cancel }}</button>
            <button
              v-else
              type="button"
              class="btn btn--sm"
              @click="remove(item.id)"
            >{{ text().remove }}</button>
          </span>
        </div>

        <p v-if="item.error" class="upload-error">{{ item.error }}</p>
      </li>
    </ul>

    <a v-if="anyDone && !busy" class="btn" href="/Files">{{ text().backToFiles }}</a>
  </div>
</template>

<style scoped>
.uploader {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.dropzone {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 34px 20px;
  border: 1px dashed var(--line);
  border-radius: 14px;
  background: var(--surface2);
  text-align: center;
  transition: border-color 120ms ease, background 120ms ease;
}

.dropzone--over {
  border-color: var(--accent);
  background: var(--soft);
}

.dropzone-title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
}

.dropzone-or,
.dropzone-hint {
  margin: 0;
  font-size: 12px;
  color: var(--muted);
}

.dropzone-hint {
  max-width: 46ch;
  line-height: 1.7;
}

/* The one sentence on this screen that is a warning rather than a description, so it is the one
   thing that is not muted grey among the notes around it. */
.upload-warning {
  margin: 0;
  font-size: 12px;
  line-height: 1.8;
  color: var(--warn);
}

.upload-concurrency {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

/* Four choices, each as wide as a number needs and no wider — .seg stretches its options, which
   would draw four buttons a quarter of the card wide holding one digit each. */
.upload-concurrency .seg {
  flex-wrap: wrap;
}

.upload-concurrency .seg-option {
  flex: 0 0 auto;
  min-width: 46px;
}

.upload-note {
  margin: 0;
  max-width: 62ch;
  font-size: 11.5px;
  line-height: 1.8;
  color: var(--muted);
}

.upload-bulk {
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

.upload-bulk-count {
  color: var(--muted);
}

.upload-bulk-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.upload-check {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  cursor: pointer;
}

/* The browser's own box, painted in the panel's accent rather than replaced. A custom checkbox is
   a control that has to re-earn focus, keyboard and indeterminate for nothing. */
.upload-check input {
  accent-color: var(--accent);
  margin: 0;
}

.upload-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.upload-row {
  padding: 11px 14px;
  border: 1px solid var(--line);
  border-radius: 12px;
  background: var(--surface);
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.upload-row--failed {
  border-color: var(--danger);
}

.upload-head {
  display: flex;
  align-items: baseline;
  gap: 12px;
}

.upload-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13.5px;
}

.upload-state {
  font-size: 11px;
  color: var(--muted);
}

.upload-row--done .upload-state {
  color: var(--accent-ink);
}

.upload-row--paused .upload-state {
  color: var(--warn);
}

.upload-row--failed .upload-state {
  color: var(--danger);
}

/* The bar moves several times a second now, so a transition would lag behind the number beside it
   rather than smooth it. Width is set directly and the browser paints it. */
.upload-meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px 14px;
  font-size: 11px;
  color: var(--muted);
}

/* The one figure the eye goes to first, so it is the one that is not muted. */
.upload-percent {
  color: var(--text);
  font-variant-numeric: tabular-nums;
  min-width: 4ch;
}

.upload-eta {
  display: inline-flex;
  gap: 4px;
  align-items: baseline;
}

.push-end {
  margin-inline-start: auto;
}

.upload-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.upload-error {
  margin: 0;
  font-size: 12px;
  line-height: 1.7;
  color: var(--danger);
}
</style>
