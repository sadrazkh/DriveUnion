<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { newRecoveryKey, type Secret } from '../crypto/envelope';
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
 * Which way a file is arriving, and the reason this is a tab rather than a second screen.
 *
 * <p>Sending a file and asking the server to go and get one are the same act to the person doing it:
 * something of theirs ends up in their workspace. They were two entirely different screens — a Vue
 * dropzone with a lock and a progress bar, and a Razor form with a password box hidden inside a
 * disclosure — and the second one read as a feature bolted to the side of the first. One control
 * chooses between them, one lock covers both, and one list shows what is arriving either way.</p>
 */
const via = ref<'file' | 'link'>('file');

const url = ref('');
const linkError = ref('');
const sending = ref(false);

/** What the server is fetching, polled — see the note on `poll`. */
interface FetchRow {
  id: string;
  url: string;
  name: string;
  status: string;
  statusText: string;
  live: boolean;
  progress: string;
  percent: number;
  known: boolean;
  error: string | null;
}

const fetches = ref<FetchRow[]>([]);

/**
 * The lock, and everything it needs before it can be used.
 *
 * <p>Per upload rather than a workspace setting, because the answer is not the same for a holiday
 * video and a scan of a passport, and a setting somebody turned on in March is not a decision they
 * are making now. None of this is sent anywhere or written anywhere: what leaves the tab is the
 * wrapped key, and what wraps it never exists outside these refs.</p>
 */
const locking = ref(false);
const custody = ref<'passphrase' | 'recoveryKey'>('passphrase');
const passphrase = ref('');
const confirmation = ref('');
const recoveryKey = ref('');
const kept = ref(false);
const copied = ref(false);

/** Eight characters is not a policy, it is the floor below which the KDF is the only defence left. */
const MinPassphrase = 8;

const tooShort = computed(() =>
  passphrase.value.length > 0 && passphrase.value.length < MinPassphrase);

const mismatched = computed(() =>
  confirmation.value.length > 0 && passphrase.value !== confirmation.value);

/**
 * Whether a file dropped now could actually be locked.
 *
 * <p>The generated key has to be acknowledged as saved before it will encrypt anything. It is the
 * only copy in the world and it exists for about four seconds before the upload starts — a tick box
 * is a thin thing to hang that on, and it is still better than the alternative, which is somebody
 * discovering the requirement after the file is already unopenable.</p>
 */
const ready = computed(() => {
  if (!locking.value) return true;

  return custody.value === 'passphrase'
    ? passphrase.value.length >= MinPassphrase && passphrase.value === confirmation.value
    : recoveryKey.value.length > 0 && kept.value;
});

const secret = (): Secret | null =>
  !locking.value
    ? null
    : custody.value === 'passphrase'
      ? { kind: 'passphrase', value: passphrase.value }
      : { kind: 'recoveryKey', value: recoveryKey.value };

function chooseCustody(kind: 'passphrase' | 'recoveryKey') {
  custody.value = kind;
  // Generated once and then left alone: regenerating on every click would quietly invalidate a key
  // somebody had already written down.
  if (kind === 'recoveryKey' && !recoveryKey.value) recoveryKey.value = newRecoveryKey();
}

async function copyKey() {
  try {
    await navigator.clipboard.writeText(recoveryKey.value);
    copied.value = true;
    setTimeout(() => (copied.value = false), 1600);
  } catch {
    // Clipboard permission refused, or an insecure origin. The field is selectable either way.
  }
}

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
        lock: 'Lock these files',
        lockHint:
          'The file is encrypted on this machine before any of it is sent, so what we store is unreadable to us and to anyone who reaches it. You are the only one who can open it again.',
        lockWarning:
          'There is no way to recover a locked file without its key. We do not have a copy and cannot make one — a lost key is a lost file.',
        custody: 'How you unlock it',
        byPassphrase: 'A passphrase you choose',
        byKey: 'A key we generate',
        passphrase: 'Passphrase',
        confirmation: 'Type it again',
        atLeast: 'At least',
        characters: 'characters.',
        mismatch: 'The two do not match.',
        yourKey: 'Your key',
        copy: 'Copy',
        copiedIt: 'Copied',
        keptIt: 'I have saved this key somewhere safe',
        notReady: 'Finish the lock above before choosing files.',
        lockedBadge: 'Locked',
        viaFile: 'Send a file',
        viaLink: 'Fetch a link',
        linkTitle: 'Paste a direct download link',
        linkHint:
          'The server fetches it for you. Your own connection is not used and this page does not have to stay open.',
        linkLabel: 'The file’s address',
        linkSend: 'Fetch it',
        linkSending: 'Asking…',
        linkFailed: 'That could not be started.',
        linkLockNote:
          'On a link the encryption happens on our server, because our server is the one fetching the file — so it sees the contents while it does. That protects it from Google and from a stolen database, and not from us. A file you send from this machine is locked before it leaves it.',
        arriving: 'Arriving',
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
        lock: 'قفل کردن این فایل‌ها',
        lockHint:
          'فایل روی همین دستگاه و پیش از آن‌که بایتی از آن برود رمز می‌شود، پس چیزی که ما ذخیره می‌کنیم برای خودِ ما و برای هرکس دیگری که به آن برسد ناخواناست. فقط شما می‌توانید بازش کنید.',
        lockWarning:
          'فایل قفل‌شده بدون کلیدش به هیچ راهی برنمی‌گردد. ما نسخه‌ای از آن نداریم و نمی‌توانیم بسازیم — کلید گم‌شده یعنی فایل گم‌شده.',
        custody: 'با چه چیزی باز شود',
        byPassphrase: 'رمزی که خودتان می‌گذارید',
        byKey: 'کلیدی که ما می‌سازیم',
        passphrase: 'رمز',
        confirmation: 'یک‌بار دیگر بنویسید',
        atLeast: 'دست‌کم',
        characters: 'کاراکتر.',
        mismatch: 'این دو یکی نیستند.',
        yourKey: 'کلید شما',
        copy: 'کپی',
        copiedIt: 'کپی شد',
        keptIt: 'این کلید را جای امنی ذخیره کردم',
        notReady: 'پیش از انتخاب فایل، قفل بالا را کامل کنید.',
        lockedBadge: 'قفل‌شده',
        viaFile: 'فرستادن فایل',
        viaLink: 'آوردن از لینک',
        linkTitle: 'لینک دانلود مستقیم را بگذارید',
        linkHint:
          'سرور خودش آن را می‌آورد. اینترنت شما درگیر نمی‌شود و لازم نیست این صفحه باز بماند.',
        linkLabel: 'نشانی فایل',
        linkSend: 'بیاورش',
        linkSending: 'در حال پرسیدن…',
        linkFailed: 'شروع نشد.',
        linkLockNote:
          'در حالت لینک، رمزگذاری روی سرور ما انجام می‌شود — چون خودِ سرور فایل را می‌آورد و در همان لحظه محتوایش را می‌بیند. این در برابر گوگل و پایگاه‌داده‌ی دزدیده‌شده محافظت می‌کند، و در برابر ما نه. فایلی که از همین دستگاه می‌فرستید پیش از رفتن قفل می‌شود.',
        arriving: 'در راه',
      };
}

const statusWord = (item: UploadItem) => text()[item.status];

const eta = (item: UploadItem) =>
  item.bytesPerSecond > 0 ? duration((item.wireSize - sent(item)) / item.bytesPerSecond) : '';

/** The antiforgery pair every write from this island carries. See UploadDock.vue. */
function headers(extra: Record<string, string> = {}): Record<string, string> {
  const config = props.config();

  return { [config.antiforgeryHeader]: config.antiforgeryToken, ...extra };
}

/**
 * Asks the server to go and get the link, and puts it in the same list as everything else.
 *
 * <p>The same endpoint the no-script form posts to, answering JSON because this asked for JSON —
 * one pair of routes for both, so the refusals cannot be worded differently in two places.</p>
 */
async function sendLink() {
  if (!ready.value || url.value.trim().length === 0) return;

  linkError.value = '';
  sending.value = true;

  try {
    const response = await fetch('/files/fetch', {
      method: 'POST',
      headers: headers({
        'Content-Type': 'application/x-www-form-urlencoded',
        Accept: 'application/json',
      }),
      body: new URLSearchParams({
        url: url.value.trim(),

        // The secret from the same lockbox the file tab uses. On this side the server does the
        // encrypting, which the box says out loud while this tab is the one showing.
        secret: locking.value && custody.value === 'passphrase'
          ? passphrase.value
          : locking.value
            ? recoveryKey.value
            : '',
      }),
    });

    const answer = (await response.json()) as { started: boolean; error: string | null };

    if (answer.started) {
      url.value = '';
      await poll();
    } else {
      linkError.value = answer.error ?? text().linkFailed;
    }
  } catch {
    linkError.value = text().linkFailed;
  } finally {
    sending.value = false;
  }
}

async function stopFetch(id: string) {
  try {
    await fetch(`/files/fetch/${id}/cancel`, {
      method: 'POST',
      headers: headers({ Accept: 'application/json' }),
    });

    await poll();
  } catch {
    // The row will say what it says at the next poll. A cancellation that did not reach the server
    // is a fetch that carries on, and inventing a stopped row here would be the screen lying.
  }
}

/**
 * Reads what the server is fetching.
 *
 * <p>Polled rather than pushed, and rarely: a fetch is carried out by a loop in this process with no
 * connection to this tab, so there is nothing local to observe. Two seconds while something is
 * moving is enough for a bar that reads as live, and the poll stops entirely when nothing is — a
 * screen left open on a finished list should not be a request every two seconds for ever.</p>
 */
async function poll() {
  try {
    const response = await fetch('/files/fetches', { headers: { Accept: 'application/json' } });
    if (!response.ok) return;

    fetches.value = ((await response.json()) as { fetches: FetchRow[] }).fetches;
  } catch {
    // Offline, or the session ended. The last list stays on screen rather than emptying, because an
    // empty list is a statement and «I could not ask» is not one.
  }
}

let timer: number | undefined;

onMounted(() => {
  void poll();

  timer = window.setInterval(() => {
    if (fetches.value.some((f) => f.live)) void poll();
  }, 2000);
});

onBeforeUnmount(() => window.clearInterval(timer));

function onDrop(event: DragEvent) {
  dragging.value = false;
  if (!ready.value) return;
  add(event.dataTransfer?.files ?? null, secret());
}

function onPicked(event: Event) {
  const el = event.target as HTMLInputElement;
  add(el.files, secret());
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
    <!--
      Which way the file is arriving. The same segmented control the concurrency choice uses, so it
      reads as a setting on this screen rather than as navigation to another one — which is what the
      two used to be, and the reason the link half read as a bolted-on feature.
    -->
    <div class="seg" role="tablist">
      <button
        type="button"
        class="seg-option"
        role="tab"
        :class="{ 'is-active': via === 'file' }"
        :aria-selected="via === 'file'"
        @click="via = 'file'"
      >{{ text().viaFile }}</button>
      <button
        type="button"
        class="seg-option"
        role="tab"
        :class="{ 'is-active': via === 'link' }"
        :aria-selected="via === 'link'"
        @click="via = 'link'"
      >{{ text().viaLink }}</button>
    </div>

    <!--
      Above whichever tab is showing, and one line tall until it is switched on.

      The order is the point. This is a decision that has to be made before the file is chosen, not
      after — it changes what happens to the bytes rather than what happens to them next — and a
      control placed below the drop target is one that gets read after it has stopped mattering.
    -->
    <div class="lockbox" :class="{ 'lockbox--on': locking }">
      <label class="upload-check">
        <input type="checkbox" v-model="locking" @change="chooseCustody(custody)" />
        <span class="lockbox-title">{{ text().lock }}</span>
      </label>

      <p class="upload-note">{{ text().lockHint }}</p>

      <div v-if="locking" class="lockbox-body">
        <p class="upload-warning">{{ text().lockWarning }}</p>

        <!--
          One lock control, two promises, and the difference said exactly when it applies.

          The same secret encrypts either way and the recipient opens both the same. What differs is
          who could have read the file on the way in — and on the link tab the honest answer is «we
          could, while we were fetching it». That used to be buried in a disclosure on a separate
          form; here it is beside the switch that makes it true.
        -->
        <p v-if="via === 'link'" class="upload-warning">{{ text().linkLockNote }}</p>

        <span class="field-label">{{ text().custody }}</span>
        <div class="seg">
          <button
            type="button"
            class="seg-option"
            :class="{ 'is-active': custody === 'passphrase' }"
            :aria-pressed="custody === 'passphrase'"
            @click="chooseCustody('passphrase')"
          >{{ text().byPassphrase }}</button>
          <button
            type="button"
            class="seg-option"
            :class="{ 'is-active': custody === 'recoveryKey' }"
            :aria-pressed="custody === 'recoveryKey'"
            @click="chooseCustody('recoveryKey')"
          >{{ text().byKey }}</button>
        </div>

        <div v-if="custody === 'passphrase'" class="lockbox-fields">
          <label class="field-label" for="lock-pass">{{ text().passphrase }}</label>
          <!-- new-password, so no manager offers the account password for a thing that is not one. -->
          <input
            id="lock-pass"
            v-model="passphrase"
            type="password"
            class="control"
            autocomplete="new-password"
          />

          <label class="field-label" for="lock-again">{{ text().confirmation }}</label>
          <input
            id="lock-again"
            v-model="confirmation"
            type="password"
            class="control"
            autocomplete="new-password"
          />

          <!--
            The figure carries its own direction, like every other number in this file. «دست‌کم ۸
            کاراکتر.» is a Persian sentence with one Latin digit in it, and a bare digit run at a
            boundary is reordered by the same rule that laid «0 B / 202 MB» out backwards.
          -->
          <p v-if="tooShort" class="upload-error">
            {{ text().atLeast }}
            <span class="mono" dir="ltr">{{ MinPassphrase }}</span>
            {{ text().characters }}
          </p>
          <p v-else-if="mismatched" class="upload-error">{{ text().mismatch }}</p>
        </div>

        <div v-else class="lockbox-fields">
          <span class="field-label">{{ text().yourKey }}</span>
          <!-- dir="ltr": base64url in groups of six, which RTL would otherwise rearrange. -->
          <div class="field" dir="ltr">
            <input
              class="control mono lockbox-key"
              readonly
              :value="recoveryKey"
              :aria-label="text().yourKey"
              @focus="($event.target as HTMLInputElement).select()"
            />
            <button type="button" class="btn btn--sm" @click="copyKey()">
              {{ copied ? text().copiedIt : text().copy }}
            </button>
          </div>

          <label class="upload-check">
            <input type="checkbox" v-model="kept" />
            <span>{{ text().keptIt }}</span>
          </label>
        </div>
      </div>
    </div>

    <div
      v-if="via === 'file'"
      class="dropzone"
      :class="{ 'dropzone--over': dragging && ready }"
      @dragenter.prevent="dragging = true"
      @dragover.prevent="dragging = true"
      @dragleave.prevent="dragging = false"
      @drop.prevent="onDrop"
    >
      <p class="dropzone-title">{{ text().drop }}</p>
      <p class="dropzone-or">{{ text().or }}</p>
      <button type="button" class="btn btn--primary" :disabled="!ready" @click="input?.click()">
        {{ text().choose }}
      </button>
      <!--
        Said rather than only shown by a greyed button: an upload the person believed was locked and
        was not is the failure this whole screen exists to prevent, so refusing is not enough — it
        has to be refused out loud.
      -->
      <p v-if="!ready" class="upload-warning">{{ text().notReady }}</p>
      <p class="dropzone-hint">{{ text().hint }}</p>
      <input ref="input" type="file" multiple hidden @change="onPicked" />
    </div>

    <!--
      The same card as the dropzone, deliberately: it is the same act and it should be the same
      shape. A field where the drop target is, the button where «Choose files» is, the same refusal
      when the lock is unfinished, and the same hint underneath.
    -->
    <div v-else class="dropzone">
      <p class="dropzone-title">{{ text().linkTitle }}</p>

      <label class="visually-hidden" for="fetch-url">{{ text().linkLabel }}</label>
      <!-- dir="ltr": a URL is a Latin run, and an RTL page reorders it into something unreadable. -->
      <input
        id="fetch-url"
        v-model="url"
        type="url"
        class="control mono linkbox-url"
        dir="ltr"
        autocomplete="off"
        placeholder="https://…"
        :disabled="sending"
        @keydown.enter.prevent="sendLink()"
      />

      <button
        type="button"
        class="btn btn--primary"
        :disabled="!ready || sending || url.trim().length === 0"
        @click="sendLink()"
      >{{ sending ? text().linkSending : text().linkSend }}</button>

      <p v-if="!ready" class="upload-warning">{{ text().notReady }}</p>
      <p v-if="linkError" class="upload-error">{{ linkError }}</p>
      <p class="dropzone-hint">{{ text().linkHint }}</p>
    </div>

    <!--
      Said here, before the first file is chosen, because the alternative is finding it out with
      90 GB sent. The queue outlives every navigation inside the panel and outlives nothing else:
      a File handle belongs to the page that opened it, and closing or reloading the tab takes it
      back. No worker can rescue one either — it would have to copy the bytes first.

      Only on the file tab. A fetch is the server's and survives this page being closed, which is
      the whole point of it — telling somebody otherwise would be the opposite of true.
    -->
    <p v-if="via === 'file'" class="upload-warning">{{ text().tabWarning }}</p>

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

    <!--
      One list, both ways in.

      Two loops rather than one over a merged model, because the actions genuinely differ — a
      browser upload can be paused and a server fetch cannot — and a shared shape that had to carry
      both would be a worse lie than two loops that produce the same row. What matters to the person
      reading it is that a thing arriving looks like a thing arriving, whichever way it came.
    -->
    <ul v-if="items.length || fetches.length" class="upload-list">
      <li
        v-for="fetch in fetches"
        :key="fetch.id"
        class="upload-row"
        :class="{ 'upload-row--failed': fetch.status === 'Failed' }"
      >
        <div class="upload-head">
          <span class="upload-name" dir="auto" :title="fetch.url">{{ fetch.name }}</span>
          <span class="badge">{{ text().viaLink }}</span>
          <span class="upload-state">{{ fetch.statusText }}</span>
        </div>

        <div
          class="bar"
          role="progressbar"
          :aria-valuenow="Math.round(fetch.percent)"
          aria-valuemin="0"
          aria-valuemax="100"
        >
          <span class="bar-fill" :style="{ width: `${fetch.percent}%` }"></span>
        </div>

        <div class="upload-meta">
          <span v-if="fetch.known" class="upload-percent mono" dir="ltr">
            {{ Math.round(fetch.percent) }}%
          </span>
          <span class="mono" dir="ltr">{{ fetch.progress }}</span>
          <!-- The address, so two fetches of files with the same name are tellable apart. -->
          <span class="mono upload-source" dir="ltr" :title="fetch.url">{{ fetch.url }}</span>

          <span class="push-end upload-actions">
            <button
              v-if="fetch.live"
              type="button"
              class="btn btn--sm btn--danger"
              @click="stopFetch(fetch.id)"
            >{{ text().cancel }}</button>
          </span>
        </div>

        <p v-if="fetch.error" class="upload-error">{{ fetch.error }}</p>
      </li>

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
          <!-- Which of these went out locked, once several batches are in one list. -->
          <span v-if="item.encrypt" class="badge">{{ text().lockedBadge }}</span>
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

/* One line high while it is off, and the same card as the rest of the screen when it is on: this is
   a choice on the way to uploading, not a section of its own. */
.lockbox {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 11px 14px;
  border: 1px solid var(--line);
  border-radius: 12px;
  background: var(--surface2);
}

.lockbox--on {
  border-color: var(--accent);
}

.lockbox-title {
  font-size: 13px;
  font-weight: 600;
}

.lockbox-body,
.lockbox-fields {
  display: flex;
  flex-direction: column;
  gap: 7px;
}

.lockbox-body {
  gap: 10px;
  margin-top: 4px;
}

/* The URL sits where the drop target does, so it needs the width the card has rather than a
   field.s default. */
.linkbox-url {
  width: 100%;
  max-width: 46ch;
}

/* The address under a fetch row: one line, and the end of it cut rather than wrapped into three.
   Somebody scanning a list is matching the start of a URL, not reading it. */
.upload-source {
  max-width: 34ch;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* The key is read character by character off the screen, so it gets the spacing that makes that
   possible rather than the panel's ordinary line height. */
.lockbox-key {
  flex: 1;
  letter-spacing: 0.04em;
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
