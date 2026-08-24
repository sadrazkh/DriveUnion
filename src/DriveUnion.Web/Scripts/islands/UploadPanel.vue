<script setup lang="ts">
import { computed, ref } from 'vue';

/**
 * The uploader. Everything the panel is sold on passes through this file.
 *
 * The server can take a 96 GB file and has been able to since M1 — `POST /api/uploads` opens a
 * resumable session, `PUT /api/uploads/{id}/chunk` forwards one piece to Drive without buffering it.
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

/** Three attempts per chunk, and only for failures a fourth could plausibly survive. */
const MaxChunkAttempts = 3;

/**
 * How far back the speed reading looks.
 *
 * The first version divided everything sent by everything elapsed, which is an average over the
 * whole transfer: it starts wrong, converges slowly, and never shows that the line just slowed down.
 * Three seconds is long enough to ride out one stalled packet and short enough to be the number the
 * word "speed" implies.
 */
const SpeedWindowMs = 3000;

type ItemStatus = 'queued' | 'uploading' | 'done' | 'failed' | 'cancelled';

interface Sample {
  at: number;
  bytes: number;
}

interface Item {
  key: number;
  file: File;
  status: ItemStatus;
  /** Bytes Drive has acknowledged, via the server. Authoritative, and only moves per chunk. */
  confirmed: number;
  /** Bytes of the chunk in flight that have left the browser. Smooth, and not yet committed. */
  inFlight: number;
  error: string;
  samples: Sample[];
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
        signedOut: 'نشست شما تمام شده. دوباره وارد شوید و آپلود را از سر بگیرید.',
        remaining: 'باقی‌مانده',
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
        signedOut: 'Your session has ended. Sign in again and restart the upload.',
        remaining: 'left',
      });

const statusText = (item: Item) => text.value[item.status];

/**
 * Decimal, because every operating system's file properties dialog is decimal and this number is
 * read against one. The plan ceilings elsewhere in the panel are binary and say so where they are
 * shown; a size the customer is comparing with their own file manager is not the place to be right
 * about 1024.
 */
function bytes(value: number): string {
  if (value < 1000) return `${Math.round(value)} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let n = value;
  let unit = -1;
  while (n >= 1000 && unit < units.length - 1) {
    n /= 1000;
    unit++;
  }
  return `${n.toFixed(n < 10 ? 1 : 0)} ${units[unit]}`;
}

function duration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '';
  if (seconds < 60) return `${Math.ceil(seconds)}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${Math.round(seconds % 60)}s`;
  return `${Math.floor(seconds / 3600)}h ${Math.round((seconds % 3600) / 60)}m`;
}

const sent = (item: Item) => Math.min(item.confirmed + item.inFlight, item.file.size);

const percent = (item: Item) =>
  item.file.size === 0 ? 0 : Math.min(100, (sent(item) / item.file.size) * 100);

const eta = (item: Item) =>
  item.bytesPerSecond > 0 ? duration((item.file.size - sent(item)) / item.bytesPerSecond) : '';

/** A rolling reading, recomputed wherever bytes move — from a chunk tick or from a server answer. */
function sample(item: Item) {
  const now = performance.now();
  item.samples.push({ at: now, bytes: sent(item) });

  while (item.samples.length > 2 && now - item.samples[0].at > SpeedWindowMs) item.samples.shift();

  const first = item.samples[0];
  const span = (now - first.at) / 1000;
  if (span > 0.2) item.bytesPerSecond = Math.max(0, (sent(item) - first.bytes) / span);
}

function add(files: FileList | null) {
  if (!files) return;

  for (const file of Array.from(files)) {
    items.value.push({
      key: nextKey++,
      file,
      // A zero-byte file has no chunk to send, so the session would open and never complete. Refused
      // here, where it can be said, rather than left to look like a stall.
      status: file.size === 0 ? 'failed' : 'queued',
      confirmed: 0,
      inFlight: 0,
      error: file.size === 0 ? text.value.emptyFile : '',
      samples: [],
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
  item.inFlight = 0;
  pump();
}

function remove(item: Item) {
  items.value = items.value.filter((i) => i.key !== item.key);
}

function retry(item: Item) {
  if (item.file.size === 0) return;
  item.abort = new AbortController();
  item.status = 'queued';
  item.confirmed = 0;
  item.inFlight = 0;
  item.samples = [];
  item.bytesPerSecond = 0;
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
    next.samples = [{ at: performance.now(), bytes: 0 }];

    void upload(next).finally(() => {
      active--;
      pump();
    });
  }
}

function authHeaders(extra: Record<string, string> = {}): Record<string, string> {
  return { [props.antiforgeryHeader]: props.antiforgeryToken, ...extra };
}

interface Answer {
  status: number;
  statusText: string;
  body: string;
  retryAfterSeconds: number;
}

/**
 * The chunk goes out over XMLHttpRequest rather than fetch, and that is the whole reason the
 * progress bar moves.
 *
 * fetch has no upload progress event — a request body is opaque until the response arrives. So the
 * bar could only step once per finished chunk, and a chunk is 32 MiB: on a 202 MB file that is
 * seven movements for the entire transfer, which reads as a frozen bar and an idle connection.
 * `xhr.upload.onprogress` reports bytes as they leave, several times a second.
 */
function putChunk(
  url: string,
  body: Blob,
  headers: Record<string, string>,
  onProgress: (sentBytes: number) => void,
  signal: AbortSignal,
): Promise<Answer> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', url, true);

    for (const [name, value] of Object.entries(headers)) xhr.setRequestHeader(name, value);

    xhr.upload.onprogress = (event) => onProgress(event.loaded);

    xhr.onload = () =>
      resolve({
        status: xhr.status,
        statusText: xhr.statusText,
        body: xhr.responseText,
        retryAfterSeconds: Number(xhr.getResponseHeader('Retry-After') ?? 0),
      });

    xhr.onerror = () => reject(new Error('network'));
    xhr.ontimeout = () => reject(new Error('network'));
    xhr.onabort = () => reject(new DOMException('aborted', 'AbortError'));

    if (signal.aborted) {
      xhr.abort();
      return;
    }
    signal.addEventListener('abort', () => xhr.abort(), { once: true });

    xhr.send(body);
  });
}

/**
 * Turns whatever the server said into one sentence.
 *
 * Two shapes arrive here. A plan refusal is 409 with `{error, limit, …}` — a code and figures, no
 * prose, because the wording belongs to the client. Everything else is a ProblemDetails whose
 * `detail` is already a finished sentence. Neither ever carries an exception message.
 */
function describe(status: number, statusText: string, raw: string): string {
  let body: Record<string, unknown> = {};
  try {
    body = JSON.parse(raw) as Record<string, unknown>;
  } catch {
    return `${status} ${statusText}`.trim();
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

  return `${status} ${statusText}`.trim();
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function upload(item: Item) {
  try {
    const begun = await fetch(props.beginUrl, {
      method: 'POST',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
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
      item.error = describe(begun.status, begun.statusText, await begun.text());
      return;
    }

    const { id, chunkSize } = (await begun.json()) as { id: string; chunkSize: number };
    const total = item.file.size;
    const chunkUrl = `${props.beginUrl.replace(/\/$/, '')}/${id}/chunk`;

    while (item.confirmed < total) {
      const from = item.confirmed;
      const to = Math.min(from + chunkSize, total);

      let attempt = 0;
      for (;;) {
        attempt++;
        item.inFlight = 0;

        let answer: Answer;
        try {
          answer = await putChunk(
            chunkUrl,
            item.file.slice(from, to),
            authHeaders({
              'Content-Type': 'application/octet-stream',
              'Content-Range': `bytes ${from}-${to - 1}/${total}`,
            }),
            (loaded) => {
              item.inFlight = loaded;
              sample(item);
            },
            item.abort.signal,
          );
        } catch (error) {
          if (item.abort.signal.aborted) return;
          item.inFlight = 0;
          if (attempt >= MaxChunkAttempts) {
            item.status = 'failed';
            item.error = text.value.networkError;
            return;
          }
          await wait(attempt * 1000);
          continue;
        }

        if (answer.status >= 200 && answer.status < 300) {
          // A 2xx that is not JSON is the sign-in page. XHR follows redirects, so a session that
          // expired mid-transfer comes back as 200 and a login form rather than as a 401 — and
          // parsing it as progress would report the connection as lost, sending the customer to
          // look at their network instead of at the header where the sign-in button is.
          let progress: { bytesConfirmed: number; status: string; failureReason: string | null };
          try {
            progress = JSON.parse(answer.body);
          } catch {
            item.status = 'failed';
            item.error = text.value.signedOut;
            return;
          }

          // The server's figure, not ours. It asks Drive what it actually acknowledged, and what we
          // sent is not evidence — a chunk can leave the browser and not be committed. Which is
          // also why inFlight is a separate number: it is honest about being provisional.
          item.confirmed = progress.bytesConfirmed;
          item.inFlight = 0;
          sample(item);

          if (progress.status === 'Failed') {
            item.status = 'failed';
            item.error = progress.failureReason ?? '';
            return;
          }

          break;
        }

        item.inFlight = 0;

        // 5xx and 429 are worth another try; a 4xx is the server saying the same thing again.
        const again = answer.status >= 500 || answer.status === 429;
        if (!again || attempt >= MaxChunkAttempts) {
          item.status = 'failed';
          item.error = describe(answer.status, answer.statusText, answer.body);
          return;
        }

        await wait(answer.retryAfterSeconds > 0 ? answer.retryAfterSeconds * 1000 : attempt * 1000);
      }
    }

    item.status = 'done';
    item.inFlight = 0;
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
          <span class="upload-state">{{ statusText(item) }}</span>
        </div>

        <div class="bar" role="progressbar" :aria-valuenow="Math.round(percent(item))" aria-valuemin="0" aria-valuemax="100">
          <span class="bar-fill" :style="{ width: `${percent(item)}%` }"></span>
        </div>

        <!--
          dir="ltr" on every run of digits and Latin units, and it is not decoration. In an RTL
          container the browser reorders a bidirectional run by its own rules, so "0 B / 202 MB"
          renders as "0 MB 202 / B" — which is what the owner saw and called unreadable. The
          direction has to be stated where the number is, not on an ancestor.
        -->
        <div class="upload-meta">
          <span class="upload-percent mono" dir="ltr">{{ Math.round(percent(item)) }}%</span>
          <span class="mono" dir="ltr">{{ bytes(sent(item)) }} / {{ bytes(item.file.size) }}</span>
          <span v-if="item.status === 'uploading' && item.bytesPerSecond > 0" class="mono" dir="ltr">
            {{ bytes(item.bytesPerSecond) }}/s
          </span>
          <span v-if="item.status === 'uploading' && eta(item)" class="upload-eta">
            <span class="mono" dir="ltr">{{ eta(item) }}</span> {{ text.remaining }}
          </span>
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
