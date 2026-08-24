<script setup lang="ts">
import { computed, ref } from 'vue';

/**
 * The uploader. Everything the panel is sold on passes through this file.
 *
 * The server can take a 96 GB file and has been able to since M1 — `POST /api/uploads` opens a
 * resumable session, `PUT /api/uploads/{id}/chunk` forwards one piece to Drive without buffering it.
 * Nothing was calling it. `Views/Files/Upload.cshtml` renders a `data-island="upload-panel"` mount
 * point with a no-JavaScript fallback inside, and `main.ts` registered no island by that name, so
 * every visitor saw the fallback and the API had no client at all.
 *
 * A `<form enctype="multipart/form-data">` is the thing this deliberately is not. That posts the
 * whole file as one request, which is the 96 GB body that must not exist: it cannot resume, it
 * cannot report progress, and it makes the size of a file a property of a proxy's patience.
 */

const props = defineProps<{
  /** `/api/uploads`. The chunk and progress routes hang off it. */
  beginUrl: string;
  antiforgeryHeader: string;
  antiforgeryToken: string;
  lang?: 'fa' | 'en';
}>();

const fa = computed(() => props.lang !== 'en');

/**
 * Two files at a time, not one and not eight.
 *
 * Each file is its own Drive resumable session, so files genuinely run in parallel — unlike chunks
 * within one file, which Drive acknowledges as a single contiguous prefix and which therefore
 * cannot. Two keeps a slow first file from blocking a queue of small ones without turning the
 * server's outbound pipe into a contention problem it has to solve.
 */
const MaxConcurrentFiles = 2;

/** Three attempts per chunk, and only for failures that a fourth could plausibly survive. */
const MaxChunkAttempts = 3;

type ItemStatus = 'queued' | 'uploading' | 'done' | 'failed' | 'cancelled';

interface Item {
  key: number;
  file: File;
  status: ItemStatus;
  sent: number;
  error: string;
  startedAt: number;
  bytesPerSecond: number;
  abort: AbortController;
}

let nextKey = 0;
const items = ref<Item[]>([]);
const dragging = ref(false);
const input = ref<HTMLInputElement | null>(null);

const anyDone = computed(() => items.value.some((i) => i.status === 'done'));
const busy = computed(() => items.value.some((i) => i.status === 'uploading' || i.status === 'queued'));

const text = computed(() =>
  fa.value
    ? {
        drop: 'فایل‌ها را این‌جا رها کنید',
        or: 'یا',
        choose: 'انتخاب فایل',
        hint: 'هر تعداد فایل، با هر حجم. هر فایل تکه‌تکه فرستاده می‌شود، پس قطع شدن وسط کار همه‌اش را هدر نمی‌دهد.',
        queued: 'در صف',
        uploading: 'در حال آپلود',
        done: 'انجام شد',
        failed: 'ناموفق',
        cancelled: 'لغو شد',
        cancel: 'لغو',
        remove: 'حذف',
        retry: 'تلاش دوباره',
        backToFiles: 'رفتن به فایل‌ها',
        emptyFile: 'این فایل خالی است و چیزی برای فرستادن ندارد.',
        networkError: 'ارتباط با سرور قطع شد.',
      }
    : {
        drop: 'Drop files here',
        or: 'or',
        choose: 'Choose files',
        hint: 'Any number of files, any size. Each one is sent in pieces, so an interruption does not cost the whole transfer.',
        queued: 'Queued',
        uploading: 'Uploading',
        done: 'Done',
        failed: 'Failed',
        cancelled: 'Cancelled',
        cancel: 'Cancel',
        remove: 'Remove',
        retry: 'Try again',
        backToFiles: 'Go to files',
        emptyFile: 'This file is empty and has nothing to send.',
        networkError: 'The connection to the server was lost.',
      });

const statusText = (item: Item) => text.value[item.status === 'uploading' ? 'uploading' : item.status];

/**
 * Decimal, because every operating system's file properties dialog is decimal and this number is
 * read against one. The plan ceilings elsewhere in the panel are binary and say so where they are
 * shown; a size the customer is comparing with their own file manager is not the place to be right
 * about 1024.
 */
function bytes(value: number): string {
  if (value < 1000) return `${value} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let n = value;
  let unit = -1;
  while (n >= 1000 && unit < units.length - 1) {
    n /= 1000;
    unit++;
  }
  return `${n.toFixed(n < 10 ? 1 : 0)} ${units[unit]}`;
}

const percent = (item: Item) =>
  item.file.size === 0 ? 0 : Math.min(100, Math.round((item.sent / item.file.size) * 100));

function add(files: FileList | null) {
  if (!files) return;

  for (const file of Array.from(files)) {
    items.value.push({
      key: nextKey++,
      file,
      // A zero-byte file has no chunk to send, so the session would open and never complete. Refused
      // here, where it can be said, rather than left to look like a stall.
      status: file.size === 0 ? 'failed' : 'queued',
      sent: 0,
      error: file.size === 0 ? text.value.emptyFile : '',
      startedAt: 0,
      bytesPerSecond: 0,
      abort: new AbortController(),
    });
  }

  pump();
}

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

function cancel(item: Item) {
  item.abort.abort();
  item.status = 'cancelled';
  pump();
}

function remove(item: Item) {
  items.value = items.value.filter((i) => i.key !== item.key);
}

function retry(item: Item) {
  if (item.file.size === 0) return;
  item.abort = new AbortController();
  item.status = 'queued';
  item.sent = 0;
  item.error = '';
  pump();
}

let active = 0;

function pump() {
  while (active < MaxConcurrentFiles) {
    const next = items.value.find((i) => i.status === 'queued');
    if (!next) return;

    active++;
    next.status = 'uploading';
    next.startedAt = performance.now();

    void upload(next).finally(() => {
      active--;
      pump();
    });
  }
}

function headers(extra: Record<string, string> = {}): Record<string, string> {
  return { [props.antiforgeryHeader]: props.antiforgeryToken, ...extra };
}

/**
 * Turns whatever the server said into one sentence.
 *
 * Two shapes arrive here. A plan refusal is 409 with `{error, limit, …}` — a code and figures, no
 * prose, because the wording belongs to the client. Everything else is a ProblemDetails whose
 * `detail` is already a finished sentence. Neither ever carries an exception message.
 */
async function describe(response: Response): Promise<string> {
  let body: Record<string, unknown> = {};
  try {
    body = (await response.json()) as Record<string, unknown>;
  } catch {
    return `${response.status} ${response.statusText}`.trim();
  }

  const code = typeof body.error === 'string' ? body.error : '';

  if (code === 'file_too_large_for_plan') {
    const max = bytes(Number(body.maxFileBytes ?? 0));
    return fa.value
      ? `این فایل از سقف هر فایل در پلن شما (${max}) بزرگ‌تر است.`
      : `This file is over your plan's per-file limit of ${max}.`;
  }

  if (code === 'tenant_quota_exceeded') {
    const cap = bytes(Number(body.capBytes ?? 0));
    const used = bytes(Number(body.usedBytes ?? 0));
    return fa.value
      ? `فضای شما پر است: ${used} از ${cap} مصرف شده. برای ادامه باید فایلی حذف کنید.`
      : `You are out of space: ${used} of ${cap} used. Delete something to continue.`;
  }

  if (typeof body.detail === 'string' && body.detail.length > 0) return body.detail;
  if (typeof body.title === 'string' && body.title.length > 0) return body.title;

  return `${response.status} ${response.statusText}`.trim();
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function upload(item: Item) {
  try {
    const begun = await fetch(props.beginUrl, {
      method: 'POST',
      headers: headers({ 'Content-Type': 'application/json' }),
      body: JSON.stringify({
        fileName: item.file.name,
        // Browsers leave `type` empty for anything they do not recognise, and the server requires
        // one. Drive is told the same thing either way.
        mimeType: item.file.type || 'application/octet-stream',
        sizeBytes: item.file.size,
      }),
      signal: item.abort.signal,
    });

    if (!begun.ok) {
      item.status = 'failed';
      item.error = await describe(begun);
      return;
    }

    const { id, chunkSize } = (await begun.json()) as { id: string; chunkSize: number };
    const total = item.file.size;
    const chunkUrl = `${props.beginUrl.replace(/\/$/, '')}/${id}/chunk`;

    while (item.sent < total) {
      const from = item.sent;
      const to = Math.min(from + chunkSize, total);

      let attempt = 0;
      for (;;) {
        attempt++;
        let response: Response;

        try {
          response = await fetch(chunkUrl, {
            method: 'PUT',
            headers: headers({
              'Content-Type': 'application/octet-stream',
              'Content-Range': `bytes ${from}-${to - 1}/${total}`,
            }),
            body: item.file.slice(from, to),
            signal: item.abort.signal,
          });
        } catch (error) {
          if (item.abort.signal.aborted) return;
          if (attempt >= MaxChunkAttempts) {
            item.status = 'failed';
            item.error = text.value.networkError;
            return;
          }
          await wait(attempt * 1000);
          continue;
        }

        if (response.ok) {
          const progress = (await response.json()) as {
            bytesConfirmed: number;
            status: string;
            failureReason: string | null;
          };

          // The server's figure, not ours. It asks Drive what it actually acknowledged, and our
          // record of what we sent is not evidence — a chunk can be sent and not committed.
          item.sent = progress.bytesConfirmed;

          const elapsed = (performance.now() - item.startedAt) / 1000;
          if (elapsed > 0) item.bytesPerSecond = item.sent / elapsed;

          if (progress.status === 'Failed') {
            item.status = 'failed';
            item.error = progress.failureReason ?? '';
            return;
          }

          break;
        }

        // 5xx and 429 are worth another try; a 4xx is the server saying the same thing again.
        const again = response.status >= 500 || response.status === 429;
        if (!again || attempt >= MaxChunkAttempts) {
          item.status = 'failed';
          item.error = await describe(response);
          return;
        }

        const retryAfter = Number(response.headers.get('Retry-After') ?? 0);
        await wait(retryAfter > 0 ? retryAfter * 1000 : attempt * 1000);
      }
    }

    item.status = 'done';
  } catch (error) {
    if (item.abort.signal.aborted) return;
    item.status = 'failed';
    item.error = text.value.networkError;
  }
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
      <p class="dropzone-title">{{ text.drop }}</p>
      <p class="dropzone-or">{{ text.or }}</p>
      <button type="button" class="btn btn--primary" @click="input?.click()">{{ text.choose }}</button>
      <p class="dropzone-hint">{{ text.hint }}</p>
      <input ref="input" type="file" multiple hidden @change="onPicked" />
    </div>

    <ul v-if="items.length" class="upload-list">
      <li v-for="item in items" :key="item.key" class="upload-row" :class="`upload-row--${item.status}`">
        <div class="upload-head">
          <span class="upload-name" :title="item.file.name">{{ item.file.name }}</span>
          <span class="upload-state mono">{{ statusText(item) }}</span>
        </div>

        <div class="bar" role="progressbar" :aria-valuenow="percent(item)" aria-valuemin="0" aria-valuemax="100">
          <span class="bar-fill" :style="{ width: `${percent(item)}%` }"></span>
        </div>

        <div class="upload-meta mono">
          <span>{{ bytes(item.sent) }} / {{ bytes(item.file.size) }}</span>
          <span v-if="item.status === 'uploading' && item.bytesPerSecond > 0">{{ bytes(item.bytesPerSecond) }}/s</span>
          <span class="push-end">
            <button v-if="item.status === 'uploading'" type="button" class="btn btn--sm" @click="cancel(item)">
              {{ text.cancel }}
            </button>
            <button
              v-else-if="item.status === 'failed' || item.status === 'cancelled'"
              type="button"
              class="btn btn--sm"
              @click="retry(item)"
            >
              {{ text.retry }}
            </button>
            <button v-if="item.status !== 'uploading'" type="button" class="btn btn--sm" @click="remove(item)">
              {{ text.remove }}
            </button>
          </span>
        </div>

        <p v-if="item.error" class="upload-error">{{ item.error }}</p>
      </li>
    </ul>

    <a v-if="anyDone && !busy" class="btn" href="/Files">{{ text.backToFiles }}</a>
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

.upload-row--failed .upload-state {
  color: var(--danger);
}

.upload-meta {
  display: flex;
  align-items: center;
  gap: 14px;
  font-size: 11px;
  color: var(--muted);
}

.push-end {
  margin-inline-start: auto;
  display: flex;
  gap: 6px;
}

.upload-error {
  margin: 0;
  font-size: 12px;
  line-height: 1.7;
  color: var(--danger);
}
</style>
