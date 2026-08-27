import { computed, markRaw, reactive, ref, watch, type Ref } from 'vue';
import { deriveWrapping, sealWith, type Secret, type Wrapping } from '../crypto/envelope';
import { cipherLength, type EncryptionHeader } from '../crypto/format';
import { cipherSource, plainSource, type ByteSource } from '../crypto/stream';

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
 *
 * It also survives a phone, as far as a phone allows. iOS suspends a web app the moment it is
 * backgrounded and WebKit has no Background Fetch, so a transfer genuinely stops when the customer
 * switches apps or the screen locks, and no amount of code changes that. What this file does is make
 * coming back work without being asked: see `interrupted` below, and `resumeStalled`.
 */

/**
 * Where a file is, and — for the two ways it can be stopped — who stopped it.
 *
 * <p><c>paused</c> and <c>interrupted</c> both mean «not moving, with bytes already committed», and
 * keeping them apart is the whole point of having two words. Only <c>pause</c> writes
 * <c>paused</c> and only a person calls <c>pause</c>, so returning to the app must never restart
 * one: a customer who stopped an upload on purpose and found it running again on their mobile data
 * would be right to call that the worst bug in the product.</p>
 *
 * <p><c>interrupted</c> is what the environment did — a request that never came back, which is what
 * a backgrounded phone, a lift and a tunnel all look like from in here. Those are picked up again
 * automatically.</p>
 *
 * <p><c>failed</c> is neither, and is deliberately not resumed: the server answered and said no. A
 * plan ceiling, a full workspace or an expired session will say the same thing again on every app
 * switch, so that one waits for a person and the «Try again» button.</p>
 */
export type UploadStatus =
  | 'queued'
  | 'uploading'
  | 'paused'
  | 'interrupted'
  | 'done'
  | 'failed'
  | 'cancelled';

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

  /**
   * Bytes on the wire, which is the file plus one tag per segment when it is encrypted.
   *
   * <p>Every number the transfer works in is this one — the chunk loop, the bar, the speed, what is
   * left. The size shown beside the name stays <c>file.size</c>, because that is the file the
   * person has. They differ by 0.0015% and they answer different questions.</p>
   */
  wireSize: number;

  /**
   * The derivation this file's key will be wrapped with, or null for a plain upload.
   *
   * <p>A function rather than a <c>Wrapping</c> because it is shared with the rest of the batch and
   * must not run until something is actually being sent: ticking the box and choosing a file are
   * two different moments, and a second of blocked CPU belongs to the second one.</p>
   */
  encrypt: (() => Promise<Wrapping>) | null;

  /** Set at <c>begin</c>. The ciphertext when encrypting, the file itself otherwise. */
  source: ByteSource | null;
}

/**
 * What the server says about a session: what storage has acknowledged, and whether it went wrong.
 *
 * <p>Named once because three places read it and all three have to agree — the progress read on the
 * way in, the answer to every chunk, and the failure that ends a transfer.</p>
 */
interface SessionProgress {
  bytesConfirmed: number;
  status: string;
  failureReason: string | null;
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

/**
 * How long after the app comes back the queue looks for stalled transfers, and why it looks twice.
 *
 * <p>The first pass catches what had already stopped while the app was away. The second exists
 * because of the other order, which is the common one on a phone: a chunk iOS killed cannot report
 * its failure until the page runs again, so the item is still saying «uploading» during the first
 * pass and only exhausts its three attempts a second or two later — with the radio still coming up.
 * Without the second pass that file sits stopped until somebody switches apps again.</p>
 *
 * <p>Both are delayed rather than immediate for the same reason: a phone that has just been unlocked
 * has a network interface before it has a working connection, and resuming into that spends the
 * chunk retries on nothing.</p>
 */
const WakeSweepDelaysMs = [500, 8000];

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

    const total = live.reduce((sum, i) => sum + i.wireSize, 0);
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

  /**
   * @param secret What these files are to be locked with, or null to send them as they are.
   *
   * <p>Taken per call rather than read from a setting the store holds, so that unticking the box
   * cannot reach back and change what a file already queued is going to be. What was chosen when
   * a file was dropped is what happens to that file.</p>
   *
   * <p>One derivation is shared by the call and made at most once — see <c>UploadItem.encrypt</c>.
   * Nothing here is written anywhere: the secret lives in this closure and dies with the tab.</p>
   */
  function add(files: FileList | File[] | null, secret: Secret | null = null) {
    if (!files) return;

    let derived: Promise<Wrapping> | null = null;
    const encrypt = secret ? () => (derived ??= deriveWrapping(secret)) : null;

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
        wireSize: encrypt ? cipherLength(file.size) : file.size,
        encrypt,
        source: null,
      }) as UploadItem);
    }

    pump();
  }

  function find(id: number) {
    return items.value.find((i) => i.id === id);
  }

  /**
   * The one thing that writes <c>paused</c>, and the only caller is a person pressing a button.
   *
   * <p>An interrupted file is pausable too, and that is not a nicety: it is how somebody says «stop
   * picking this back up». Selecting everything and pressing Pause on a flaky connection has to
   * settle the whole list, and a file that was between attempts at that moment would otherwise be
   * the one that started again by itself two minutes later.</p>
   */
  function pause(id: number) {
    const item = find(id);

    if (!item || (item.status !== 'uploading' && item.status !== 'queued'
      && item.status !== 'interrupted')) return;

    // Abort the chunk in flight and keep what the server confirmed. Nothing is lost: the bytes the
    // abort discarded were never committed, and a resume asks Drive what it actually has.
    item.abort.abort();
    item.abort = new AbortController();
    item.status = 'paused';
    item.inFlight = 0;
    item.samples = [];
    item.bytesPerSecond = 0;
    // Whatever the interruption said about itself is no longer why this is stopped.
    item.error = '';
    pump();
  }

  function resume(id: number) {
    const item = find(id);
    if (!item || (item.status !== 'paused' && item.status !== 'interrupted')) return;

    // No new AbortController here, from either state: `pause` already put a fresh one in place of
    // the one it aborted, and an interruption never aborted anything — it is the answer that did
    // not arrive rather than a request somebody stopped.
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
    // A retry is a new upload, and an encrypted one gets a new content key when begin() runs again.
    item.source = null;
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

  // ── coming back to the app ────────────────────────────────────────────────────────────────────

  /**
   * Everything the phone stopped, put back in the queue. Nothing a person stopped.
   *
   * <p>Only <c>interrupted</c> is touched, which is the whole of the safety here: a deliberate pause
   * is a different word (see <see cref="UploadStatus"/>) and a refusal the server actually gave is a
   * third, so neither is reachable from this loop.</p>
   *
   * <p>There is no stagger and there does not need to be. Resuming marks a file <c>queued</c>, and
   * <c>pump</c> starts at most <c>concurrency</c> of those — so thirty stalled files coming back at
   * once is thirty files waiting their turn on a phone's connection, exactly as thirty freshly
   * dropped ones would be. Staggering on top of that would be a second answer to a question that
   * already has one.</p>
   */
  function resumeStalled() {
    // Hidden means the app is not in front, and on iOS not in front means frozen — a resume issued
    // now is a request that cannot leave and a chunk retry spent on nothing. The next visibility
    // change runs this again.
    if (document.visibilityState !== 'visible') return;

    // Believed only when it says no. `onLine` false is «this device has no network interface at
    // all», which is worth not resuming into; true means no more than «an interface exists», which
    // on a phone holding one bar of a captive wifi is not a claim worth acting on.
    if (navigator.onLine === false) return;

    for (const item of items.value.filter((i) => i.status === 'interrupted')) resume(item.id);
  }

  /** Sweeps already booked, so three events arriving together do not make six passes. */
  const booked = new Set<number>();

  function sweepAfterWaking() {
    for (const delay of WakeSweepDelaysMs) {
      if (booked.has(delay)) continue;

      booked.add(delay);
      setTimeout(() => {
        booked.delete(delay);
        resumeStalled();
      }, delay);
    }
  }

  /**
   * The screen kept awake while bytes are moving, and nothing beyond that.
   *
   * <p>A phone whose display times out suspends the page, and a suspended page is a stopped
   * transfer — so a lock is what lets somebody start a large upload and put the phone down. It is
   * not a background permission and there is no such thing on iOS: the browser takes the lock away
   * the moment the app is not in front, which is why it is asked for again on every return to
   * visible rather than held once.</p>
   *
   * <p>Safari has had it since 16.4, so every path here is allowed to do nothing. A refusal — an
   * older phone, a battery-saver policy, a document that went hidden mid-request — is not a failure
   * of the upload and is not reported as one.</p>
   */
  let screenLock: WakeLockSentinel | null = null;
  let asking = false;

  async function keepScreenAwake() {
    // The browser takes the lock away by itself when the page is hidden, and hands back a sentinel
    // that says so rather than a null. Forgetting a spent one is what makes the next return to
    // visible ask again instead of holding a reference to a lock that stopped existing.
    if (screenLock?.released === true) screenLock = null;

    // Asked on every return to visible, including the ones where nothing is uploading. A lock taken
    // then is a screen that will not dim on a screen nobody is transferring anything from.
    if (!busy.value || screenLock !== null || asking) return;

    if (!('wakeLock' in navigator)) return;

    // request() rejects outright on a hidden document, so this is not a precaution — it is the
    // difference between asking and throwing.
    if (document.visibilityState !== 'visible') return;

    asking = true;

    try {
      const sentinel = await navigator.wakeLock.request('screen');

      // The queue may have emptied while the request was in flight. A lock nobody is waiting on is
      // a screen that never dims again.
      if (!busy.value) {
        void sentinel.release().catch(() => undefined);
        return;
      }

      screenLock = sentinel;
    } catch {
      // Refused, or unavailable. Either way the upload is unaffected and the screen behaves as it
      // did before this feature existed.
    } finally {
      asking = false;
    }
  }

  function letScreenSleep() {
    const sentinel = screenLock;
    if (sentinel === null) return;

    screenLock = null;
    void sentinel.release().catch(() => undefined);
  }

  // Synchronous, so the lock is taken in the same turn the first chunk starts rather than a tick
  // later. `busy` is a boolean over the whole queue and changes only when the queue starts or stops
  // moving, so «sync» here is a handful of calls per upload rather than one per progress event.
  watch(busy, (moving) => (moving ? void keepScreenAwake() : letScreenSleep()), { flush: 'sync' });

  /**
   * The three events that mean «the app is back», and the ones deliberately not listened to.
   *
   * <p><b>visibilitychange</b> is the one that matters. It is what iOS fires when a web app returns
   * to the foreground, whether the customer switched apps, unlocked the screen or came back to the
   * tab, and it is the only such signal WebKit gives.</p>
   *
   * <p><b>pageshow</b> covers the restore that visibilitychange can miss: a page brought back from
   * the back/forward cache resumes with its sockets long dead, and Safari has not always marked it
   * hidden on the way in. It costs one listener to not depend on that.</p>
   *
   * <p><b>online</b> is the other reason a transfer stops on a phone, and the one the customer
   * cannot see the cause of — a lift, a tunnel, a train. Without it a file that stalled while the
   * app was in front waits for somebody to switch apps and come back, which is a strange thing to
   * have to teach anybody.</p>
   *
   * <p>Not <c>focus</c>: on a desktop it fires on every click back into the window, which is several
   * times a minute and carries nothing visibilitychange has not already said. Not the Page Lifecycle
   * <c>resume</c> event: Chromium only, and the phone this phase is about is not running Chromium.</p>
   */
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState !== 'visible') return;

    void keepScreenAwake();
    sweepAfterWaking();
  });

  window.addEventListener('pageshow', () => {
    void keepScreenAwake();
    sweepAfterWaking();
  });

  window.addEventListener('online', sweepAfterWaking);

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

  /**
   * Stopped by the environment rather than by anybody, and the line is exactly one thing: no answer
   * came back at all.
   *
   * <p>That is what decides whether a file resumes by itself. A server that answered has said
   * something — a plan ceiling, a full workspace, an expired session — and saying it again on every
   * app switch would not change it, so those stay <c>failed</c>. A request that produced no response
   * is what a backgrounded phone, a lift and a dropped wifi all look like from in here, and it is
   * the only thing <c>resumeStalled</c> will pick up.</p>
   */
  function stall(item: UploadItem) {
    item.status = 'interrupted';
    item.error = text().interrupted;
    item.inFlight = 0;
    item.samples = [];
    item.bytesPerSecond = 0;
  }

  async function run(item: UploadItem) {
    try {
      if (!item.sessionId && !(await begin(item))) return;

      // Whatever the server has, not what we think we sent. On a resume this is the whole point:
      // the abort that paused it may have raced a chunk the server was already committing.
      if (!(await syncConfirmed(item))) return;

      // begin() is what sets it, and begin() has either run or the session predates a pause.
      const source = item.source;
      if (!source) return;

      const total = item.wireSize;
      const config = readConfig();
      const chunkUrl = `${config.beginUrl.replace(/\/$/, '')}/${item.sessionId}/chunk`;

      while (item.confirmed < total) {
        if (item.status !== 'uploading') return;

        const from = item.confirmed;
        const to = Math.min(from + item.chunkSize, total);

        if (!(await sendChunk(item, source, chunkUrl, from, to, total))) return;
      }

      item.status = 'done';
      item.inFlight = 0;
    } catch {
      if (item.abort.signal.aborted) return;

      // Everything inside that block which can throw without being an abort is a `fetch` that never
      // answered — begin and the progress read. Both of them now handle a response that arrived and
      // was not JSON themselves, so reaching here means the request did not arrive.
      stall(item);
    }
  }

  async function begin(item: UploadItem): Promise<boolean> {
    const config = readConfig();

    let encryption: EncryptionHeader | null = null;

    if (item.encrypt) {
      // Here rather than at add(): this is the first moment the file is genuinely on its way, and
      // a queue of thirty files should not spend thirty seconds sealing before the first one moves.
      const sealed = await sealWith(await item.encrypt(), item.file.size);

      encryption = sealed.header;

      // markRaw: the source holds a CryptoKey and a Blob, and Vue has no business proxying either.
      item.source = markRaw(cipherSource(item.file, sealed.key, sealed.header));
    } else {
      item.source = markRaw(plainSource(item.file));
    }

    item.wireSize = item.source.size;

    const response = await fetch(config.beginUrl, {
      method: 'POST',
      headers: headers({ 'Content-Type': 'application/json' }),
      body: JSON.stringify({
        fileName: item.file.name,
        // Browsers leave `type` empty for anything they do not recognise, and the server needs one.
        mimeType: item.file.type || 'application/octet-stream',
        // The ciphertext's length, because that is what the operator stores and what the plan is
        // measured against. The real one is in the header, and is what the panel shows.
        sizeBytes: item.wireSize,
        encryption,
      }),
      signal: item.abort.signal,
    });

    if (!response.ok) {
      item.status = 'failed';
      item.error = describe(response.status, response.statusText, await response.text());
      return false;
    }

    // A 2xx that is not JSON is the sign-in page, the same way it is at the chunk endpoint: fetch
    // follows redirects, so a session that expired between two files arrives here as 200 and a login
    // form. Read as a session it would leave the transfer waiting on an id that does not exist — and
    // it is a refusal that was given rather than a request that never came back, so it fails here
    // instead of stalling and being resumed for ever on every app switch.
    let begun: { id: string; chunkSize: number };
    try {
      begun = (await response.json()) as { id: string; chunkSize: number };
    } catch {
      item.status = 'failed';
      item.error = text().signedOut;
      return false;
    }

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

    // The sign-in page again. See begin().
    let progress: SessionProgress;
    try {
      progress = (await response.json()) as SessionProgress;
    } catch {
      item.status = 'failed';
      item.error = text().signedOut;
      return false;
    }

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
    source: ByteSource,
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
          // Encrypting happens here, a window at a time, and never before it is needed: a resumed
          // upload seals the chunk it is about to send rather than the ones it already has.
          await source.slice(from, to),
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
          // Three attempts and no answer to any of them. This used to say «failed», which on a phone
          // meant that switching apps for a minute turned an upload into an error the customer had
          // to notice and press a button about. It is the environment, not a refusal, so it is
          // stopped in the state that comes back on its own.
          stall(item);
          return false;
        }
        await wait(attempt * 1000);
        continue;
      }

      if (answer.status >= 200 && answer.status < 300) {
        // A 2xx that is not JSON is the sign-in page: XHR follows redirects, so a session that
        // expired mid-transfer arrives as 200 and a login form.
        let progress: SessionProgress;
        try {
          progress = JSON.parse(answer.body) as SessionProgress;
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
          interrupted: 'ارتباط قطع شد. وقتی به برنامه برگردید، از همین‌جا خودش ادامه می‌دهد.',
        }
      : {
          emptyFile: 'This file is empty and has nothing to send.',
          networkError: 'The connection to the server was lost.',
          signedOut: 'Your session has ended. Sign in again and restart the upload.',
          interrupted:
            'The connection stopped. This carries on from here by itself when you come back.',
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
    // Exported so the behaviour is reachable without a browser to switch apps in. Nothing in the
    // panel calls it: the events above are what run it in a page.
    resumeStalled,
    pauseSelected: () => forEachSelected(pause),
    resumeSelected: () => forEachSelected(resume),
    cancelSelected: () => forEachSelected(cancel),
  };
}

export type UploadStore = ReturnType<typeof createUploadStore>;

export const sent = (item: UploadItem) => Math.min(item.confirmed + item.inFlight, item.wireSize);

export const percentOf = (item: UploadItem) =>
  item.wireSize === 0 ? 0 : Math.min(100, (sent(item) / item.wireSize) * 100);

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
