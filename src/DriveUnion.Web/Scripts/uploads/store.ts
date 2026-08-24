import { computed, reactive, ref, type Ref } from 'vue';

/**
 * The upload queue, and the only place it lives.
 *
 * It is created once by main.ts and hangs off the shell, above the content that navigation swaps.
 * That is the whole reason background uploading works: a File handle does not survive a page load,
 * and a Service Worker cannot rescue one because it would have to copy the bytes first — which for
 * a 96 GB file is not a trade. So the page stops reloading and this object stops being torn down.
 *
 * Two views read it and neither owns it: the dock in the shell, and the upload screen. A page that
 * owned the queue would end it on the way out, which is the bug this replaces.
 */

export type UploadStatus = 'queued' | 'uploading' | 'paused' | 'done' | 'failed' | 'cancelled';

export interface UploadItem {
  readonly id: number;
  readonly file: File;
  status: UploadStatus;
  /** Bytes Drive has acknowledged, via the server. Authoritative, and only moves per chunk. */
  confirmed: number;
  /** Bytes of the chunk in flight that have left the browser. Smooth, and not yet committed. */
  inFlight: number;
  error: string;
  bytesPerSecond: number;
  selected: boolean;
  /** The server's session, once opened. Kept so a pause can resume against it. */
  sessionId: string | null;
  chunkSize: number;
  samples: { at: number; bytes: number }[];
  abort: AbortController;
}

export interface UploadConfig {
  beginUrl: string;
  antiforgeryHeader: string;
  antiforgeryToken: string;
  lang: 'fa' | 'en';
}

/** How far back the speed reading looks. Long enough to ride out one stall, short enough to mean it. */
const SpeedWindowMs = 3000;

/** Three attempts per chunk, and only for failures a fourth could plausibly survive. */
const MaxChunkAttempts = 3;

/**
 * How many files move at once, and why it is a choice rather than a constant.
 *
 * Files genuinely run in parallel — each is its own Drive resumable session. Chunks within one file
 * cannot: Drive acknowledges a single contiguous prefix, so a second writer into one session has
 * nothing to write. So this is the only concurrency the product has, and a download manager's
 * answer — let the person pick — is right for the same reason it is there: they know whether they
 * are on an office line or a phone.
 */
export const ConcurrencyChoices = [1, 2, 3, 5] as const;

const ConcurrencyKey = 'driveunion.upload.concurrency';

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

export function createUploadStore(readConfig: () => UploadConfig) {
  const items = ref<UploadItem[]>([]);
  const concurrency = ref(readStoredConcurrency());
  let nextId = 0;
  let active = 0;

  const fa = () => readConfig().lang !== 'en';

  const inFlightItems = computed(() =>
    items.value.filter((i) => i.status === 'uploading' || i.status === 'queued'));

  const busy = computed(() => inFlightItems.value.length > 0);

  const totalPercent = computed(() => {
    const live = items.value.filter((i) => i.status !== 'cancelled');
    if (live.length === 0) return 0;

    const total = live.reduce((sum, i) => sum + i.file.size, 0);
    if (total === 0) return 0;

    return Math.min(100, (live.reduce((sum, i) => sum + sent(i), 0) / total) * 100);
  });

  const selected = computed(() => items.value.filter((i) => i.selected));

  function readStoredConcurrency(): number {
    const stored = Number(localStorage.getItem(ConcurrencyKey));
    return (ConcurrencyChoices as readonly number[]).includes(stored) ? stored : 2;
  }

  function setConcurrency(value: number) {
    concurrency.value = (ConcurrencyChoices as readonly number[]).includes(value) ? value : 2;
    localStorage.setItem(ConcurrencyKey, String(concurrency.value));
    // Raising it has to start something; lowering it does not stop anything already moving, because
    // aborting a chunk to honour a preference throws away bytes that were nearly committed.
    pump();
  }

  function add(files: FileList | File[] | null) {
    if (!files) return;

    for (const file of Array.from(files)) {
      items.value.push(reactive({
        id: nextId++,
        file,
        // A zero-byte file has no chunk to send, so the session would open and never complete.
        // Refused here, where it can be explained, rather than left to look like a stall.
        status: file.size === 0 ? 'failed' : 'queued',
        confirmed: 0,
        inFlight: 0,
        error: file.size === 0 ? text().emptyFile : '',
        bytesPerSecond: 0,
        selected: false,
        sessionId: null,
        chunkSize: 0,
        samples: [],
        abort: new AbortController(),
      }) as UploadItem);
    }

    pump();
  }

  function find(id: number) {
    return items.value.find((i) => i.id === id);
  }

  function pause(id: number) {
    const item = find(id);
    if (!item || (item.status !== 'uploading' && item.status !== 'queued')) return;

    // Abort the chunk in flight and keep what the server confirmed. Nothing is lost: the bytes the
    // abort discarded were never committed, and a resume asks Drive what it actually has.
    item.abort.abort();
    item.abort = new AbortController();
    item.status = 'paused';
    item.inFlight = 0;
    item.samples = [];
    item.bytesPerSecond = 0;
    pump();
  }

  function resume(id: number) {
    const item = find(id);
    if (!item || item.status !== 'paused') return;

    item.status = 'queued';
    item.error = '';
    pump();
  }

  function cancel(id: number) {
    const item = find(id);
    if (!item) return;

    item.abort.abort();
    item.status = 'cancelled';
    item.inFlight = 0;
    pump();
  }

  function remove(id: number) {
    const item = find(id);
    if (item && (item.status === 'uploading' || item.status === 'queued')) item.abort.abort();
    items.value = items.value.filter((i) => i.id !== id);
    pump();
  }

  function retry(id: number) {
    const item = find(id);
    if (!item || item.file.size === 0) return;

    item.abort = new AbortController();
    item.status = 'queued';
    item.confirmed = 0;
    item.inFlight = 0;
    item.sessionId = null;
    item.samples = [];
    item.bytesPerSecond = 0;
    item.error = '';
    pump();
  }

  function clearFinished() {
    items.value = items.value.filter(
      (i) => i.status !== 'done' && i.status !== 'cancelled');
  }

  const forEachSelected = (act: (id: number) => void) => {
    for (const item of [...selected.value]) act(item.id);
  };

  function pump() {
    while (active < concurrency.value) {
      const next = items.value.find((i) => i.status === 'queued');
      if (!next) return;

      active++;
      next.status = 'uploading';
      next.samples = [{ at: performance.now(), bytes: sent(next) }];

      void run(next).finally(() => {
        active--;
        pump();
      });
    }
  }

  function sample(item: UploadItem) {
    const now = performance.now();
    item.samples.push({ at: now, bytes: sent(item) });

    while (item.samples.length > 2 && now - item.samples[0].at > SpeedWindowMs) item.samples.shift();

    const first = item.samples[0];
    const span = (now - first.at) / 1000;
    if (span > 0.2) item.bytesPerSecond = Math.max(0, (sent(item) - first.bytes) / span);
  }

  function headers(extra: Record<string, string> = {}): Record<string, string> {
    const config = readConfig();
    return { [config.antiforgeryHeader]: config.antiforgeryToken, ...extra };
  }

  async function run(item: UploadItem) {
    try {
      if (!item.sessionId && !(await begin(item))) return;

      // Whatever the server has, not what we think we sent. On a resume this is the whole point:
      // the abort that paused it may have raced a chunk the server was already committing.
      if (!(await syncConfirmed(item))) return;

      const total = item.file.size;
      const config = readConfig();
      const chunkUrl = `${config.beginUrl.replace(/\/$/, '')}/${item.sessionId}/chunk`;

      while (item.confirmed < total) {
        if (item.status !== 'uploading') return;

        const from = item.confirmed;
        const to = Math.min(from + item.chunkSize, total);

        if (!(await sendChunk(item, chunkUrl, from, to, total))) return;
      }

      item.status = 'done';
      item.inFlight = 0;
    } catch {
      if (item.abort.signal.aborted) return;
      item.status = 'failed';
      item.error = text().networkError;
    }
  }

  async function begin(item: UploadItem): Promise<boolean> {
    const config = readConfig();

    const response = await fetch(config.beginUrl, {
      method: 'POST',
      headers: headers({ 'Content-Type': 'application/json' }),
      body: JSON.stringify({
        fileName: item.file.name,
        // Browsers leave `type` empty for anything they do not recognise, and the server needs one.
        mimeType: item.file.type || 'application/octet-stream',
        sizeBytes: item.file.size,
      }),
      signal: item.abort.signal,
    });

    if (!response.ok) {
      item.status = 'failed';
      item.error = describe(response.status, response.statusText, await response.text());
      return false;
    }

    const begun = (await response.json()) as { id: string; chunkSize: number };
    item.sessionId = begun.id;
    item.chunkSize = begun.chunkSize;
    return true;
  }

  /** Asks the server what Drive has acknowledged, and believes it over our own count. */
  async function syncConfirmed(item: UploadItem): Promise<boolean> {
    const config = readConfig();
    const url = `${config.beginUrl.replace(/\/$/, '')}/${item.sessionId}`;

    const response = await fetch(url, { headers: headers(), signal: item.abort.signal });

    if (!response.ok) {
      item.status = 'failed';
      item.error = describe(response.status, response.statusText, await response.text());
      return false;
    }

    const progress = (await response.json()) as {
      bytesConfirmed: number;
      status: string;
      failureReason: string | null;
    };

    if (progress.status === 'Failed') {
      item.status = 'failed';
      item.error = progress.failureReason ?? text().networkError;
      return false;
    }

    item.confirmed = progress.bytesConfirmed;
    return true;
  }

  async function sendChunk(
    item: UploadItem,
    chunkUrl: string,
    from: number,
    to: number,
    total: number,
  ): Promise<boolean> {
    for (let attempt = 1; ; attempt++) {
      item.inFlight = 0;

      let answer: XhrAnswer;
      try {
        answer = await putChunk(
          chunkUrl,
          item.file.slice(from, to),
          headers({
            'Content-Type': 'application/octet-stream',
            'Content-Range': `bytes ${from}-${to - 1}/${total}`,
          }),
          (loaded) => {
            item.inFlight = loaded;
            sample(item);
          },
          item.abort.signal,
        );
      } catch {
        // A pause and a lost connection both land here; only one of them is a failure.
        if (item.abort.signal.aborted || item.status !== 'uploading') return false;

        item.inFlight = 0;
        if (attempt >= MaxChunkAttempts) {
          item.status = 'failed';
          item.error = text().networkError;
          return false;
        }
        await wait(attempt * 1000);
        continue;
      }

      if (answer.status >= 200 && answer.status < 300) {
        // A 2xx that is not JSON is the sign-in page: XHR follows redirects, so a session that
        // expired mid-transfer arrives as 200 and a login form.
        let progress: { bytesConfirmed: number; status: string; failureReason: string | null };
        try {
          progress = JSON.parse(answer.body);
        } catch {
          item.status = 'failed';
          item.error = text().signedOut;
          return false;
        }

        item.confirmed = progress.bytesConfirmed;
        item.inFlight = 0;
        sample(item);

        if (progress.status === 'Failed') {
          item.status = 'failed';
          item.error = progress.failureReason ?? '';
          return false;
        }

        return true;
      }

      item.inFlight = 0;

      const again = answer.status >= 500 || answer.status === 429;
      if (!again || attempt >= MaxChunkAttempts) {
        item.status = 'failed';
        item.error = describe(answer.status, answer.statusText, answer.body);
        return false;
      }

      await wait(answer.retryAfterSeconds > 0 ? answer.retryAfterSeconds * 1000 : attempt * 1000);
    }
  }

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
      return fa()
        ? `این فایل از سقف هر فایل در پلن شما (${max}) بزرگ‌تر است.`
        : `This file is over your plan's per-file limit of ${max}.`;
    }

    if (code === 'tenant_quota_exceeded') {
      const cap = bytes(Number(body.capBytes ?? 0));
      const used = bytes(Number(body.usedBytes ?? 0));
      return fa()
        ? `فضای شما پر است: ${used} از ${cap} مصرف شده. برای ادامه باید فایلی حذف کنید.`
        : `You are out of space: ${used} of ${cap} used. Delete something to continue.`;
    }

    if (typeof body.detail === 'string' && body.detail.length > 0) return body.detail;
    if (typeof body.title === 'string' && body.title.length > 0) return body.title;

    return `${status} ${statusText}`.trim();
  }

  function text() {
    return fa()
      ? {
          emptyFile: 'این فایل خالی است و چیزی برای فرستادن ندارد.',
          networkError: 'ارتباط با سرور قطع شد.',
          signedOut: 'نشست شما تمام شده. دوباره وارد شوید و آپلود را از سر بگیرید.',
        }
      : {
          emptyFile: 'This file is empty and has nothing to send.',
          networkError: 'The connection to the server was lost.',
          signedOut: 'Your session has ended. Sign in again and restart the upload.',
        };
  }

  return {
    items: items as Ref<UploadItem[]>,
    concurrency,
    inFlightItems,
    busy,
    totalPercent,
    selected,
    add,
    pause,
    resume,
    cancel,
    remove,
    retry,
    clearFinished,
    setConcurrency,
    pauseSelected: () => forEachSelected(pause),
    resumeSelected: () => forEachSelected(resume),
    cancelSelected: () => forEachSelected(cancel),
  };
}

export type UploadStore = ReturnType<typeof createUploadStore>;

export const sent = (item: UploadItem) => Math.min(item.confirmed + item.inFlight, item.file.size);

export const percentOf = (item: UploadItem) =>
  item.file.size === 0 ? 0 : Math.min(100, (sent(item) / item.file.size) * 100);

/**
 * Decimal, because every operating system's file properties dialog is decimal and this number is
 * read against one. The plan ceilings elsewhere are binary and say so where they are shown.
 */
export function bytes(value: number): string {
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

export function duration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '';
  if (seconds < 60) return `${Math.ceil(seconds)}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${Math.round(seconds % 60)}s`;
  return `${Math.floor(seconds / 3600)}h ${Math.round((seconds % 3600) / 60)}m`;
}

interface XhrAnswer {
  status: number;
  statusText: string;
  body: string;
  retryAfterSeconds: number;
}

/**
 * The chunk goes out over XMLHttpRequest rather than fetch, and that is why the bar moves.
 *
 * fetch has no upload progress event — a request body is opaque until the response arrives — so the
 * bar could only step once per finished chunk, and a chunk is 32 MiB.
 */
function putChunk(
  url: string,
  body: Blob,
  headers: Record<string, string>,
  onProgress: (sentBytes: number) => void,
  signal: AbortSignal,
): Promise<XhrAnswer> {
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
