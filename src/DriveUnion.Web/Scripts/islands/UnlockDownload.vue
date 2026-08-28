<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import { unseal, type Secret } from '../crypto/envelope';
import { decryptInto, type DecryptFailure } from '../crypto/stream';
import { closeStream, openStream } from '../crypto/play';
import type { Bytes, EncryptionHeader } from '../crypto/format';

/**
 * The other end of the lock: the download page for a file the operator cannot read.
 *
 * <p>Everything on this screen happens in the tab. The header sits in the page already — it is not
 * secret and holding it back would only cost a round trip — so a wrong key is refused here, before a
 * byte is requested, by trying to unwrap the content key with it. That is the whole check: AES-GCM's
 * tag either verifies or it does not, and there is nothing in between to learn from.</p>
 *
 * <p>What the server sees is one ordinary download of one ordinary file. It counts against the
 * link's cap and the workspace's egress exactly as an unencrypted one does, because as far as it is
 * concerned that is what it is.</p>
 */

const props = defineProps<{
  header: EncryptionHeader;
  downloadUrl: string;
  fileName: string;

  /**
   * 'video', 'audio', or empty for a file no browser can play.
   *
   * <p>Decided by the view and not here: whether a type is safe to hand to a media element is the
   * same judgement `Previews` makes for unlocked files, and it is made in one place.</p>
   */
  media: string;

  /** The recorded type, for the element the browser builds. Empty when there is nothing to play. */
  mimeType: string;

  lang: 'fa' | 'en';
}>();

type Phase = 'asking' | 'unlocking' | 'working' | 'done' | 'playing';

const secret = ref('');
const phase = ref<Phase>('asking');
const error = ref('');
const written = ref(0);

const fa = computed(() => props.lang !== 'en');
const percent = computed(() =>
  props.header.plaintextLength === 0
    ? 0
    : Math.min(100, (written.value / props.header.plaintextLength) * 100));

/**
 * Whether this browser can write the file as it arrives instead of holding all of it.
 *
 * <p>Without it the plaintext is assembled in memory and handed over as one blob, which is fine for
 * a document and is not fine for the files this product exists for. The limit is stated on the
 * screen rather than discovered when the tab dies at eleven gigabytes.</p>
 */
const canStreamToDisk = 'showSaveFilePicker' in globalThis;

/** Past which the in-memory path is worth warning about. Not a refusal — some machines manage it. */
const MemoryWarningBytes = 512 * 1024 * 1024;

const heavy = computed(() => !canStreamToDisk && props.header.plaintextLength > MemoryWarningBytes);

function text() {
  return fa.value
    ? {
        locked: 'این فایل قفل است',
        explain:
          'محتوای این فایل روی دستگاه فرستنده رمز شده و ما نسخه‌ی خوانا‌ی آن را نداریم. رمز یا کلیدی که فرستنده به شما داده را وارد کنید تا همین‌جا در مرورگر خودتان باز شود.',
        label: 'رمز یا کلید',
        unlock: 'باز کردن و دانلود',
        checking: 'در حال بررسی…',
        wrongKey: 'این رمز درست نیست.',
        needSecret: 'رمز یا کلید را وارد کنید.',
        working: 'در حال دانلود و رمزگشایی',
        corrupt: 'این فایل با این کلید باز شد اما محتوایش سالم نیست و ادامه‌اش داده نشد.',
        truncated: 'دانلود نیمه‌کاره ماند و فایل کامل نشد. دوباره تلاش کنید.',
        failed: 'دانلود انجام نشد. دوباره تلاش کنید.',
        cancelled: 'جایی برای ذخیره انتخاب نشد.',
        done: 'فایل باز شد و ذخیره شد.',
        again: 'یک‌بار دیگر',
        play: 'پخش همین‌جا',
        saveCopy: 'ذخیره‌ی یک نسخه',
        streaming: 'همین‌جا و در مرورگر خودتان رمزگشایی می‌شود. هیچ نسخه‌ی بازی جایی ذخیره نمی‌شود.',
        memory:
          'مرورگر شما نمی‌تواند فایل را همان‌طور که می‌رسد روی دیسک بنویسد، پس تمام آن باید در حافظه جمع شود. برای فایلی به این بزرگی ممکن است تب از کار بیفتد؛ با کروم یا اج نتیجه بهتر است.',
      }
    : {
        locked: 'This file is locked',
        explain:
          'It was encrypted on the sender’s machine and we hold no readable copy. Enter the passphrase or key they gave you and it will be opened here, in your own browser.',
        label: 'Passphrase or key',
        unlock: 'Unlock and download',
        checking: 'Checking…',
        wrongKey: 'That is not the right key.',
        needSecret: 'Enter the passphrase or key.',
        working: 'Downloading and decrypting',
        corrupt: 'This key opened the file but its contents did not verify, so it was stopped.',
        truncated: 'The download ended early and the file is incomplete. Try again.',
        failed: 'The download did not finish. Try again.',
        cancelled: 'No place to save was chosen.',
        done: 'Opened and saved.',
        again: 'Try another',
        play: 'Play it here',
        saveCopy: 'Save a copy',
        streaming: 'Decrypted here, in your own browser, as it plays. No readable copy is stored anywhere.',
        memory:
          'Your browser cannot write the file to disk as it arrives, so all of it has to be held in memory. For a file this size the tab may not survive it; Chrome or Edge will do better.',
      };
}

/**
 * The secret is one field, not two, and that is the format's doing rather than a shortcut.
 *
 * <p>A generated key and a chosen passphrase go through the same derivation — see envelope.ts, which
 * says why — so the person on this end never has to know which kind they were handed. They paste
 * what they were given.</p>
 */
const asSecret = (): Secret => ({ kind: 'passphrase', value: secret.value });

async function unlock() {
  if (secret.value.length === 0) {
    error.value = text().needSecret;
    return;
  }

  error.value = '';
  phase.value = 'unlocking';

  const key = await unseal(asSecret(), props.header);

  if (!key) {
    // Nothing has been requested yet, so a wrong key costs the visitor a second and costs the
    // owner's download cap nothing at all.
    phase.value = 'asking';
    error.value = text().wrongKey;
    return;
  }

  // A file the browser can play is played rather than saved, and this is the whole of P7b from the
  // reader's side. The alternative — what happened until now — is a two-hour wait on a progress bar
  // before the first frame, and no way to skip to the middle because there is no middle until the
  // end has arrived.
  //
  // Nothing is said when this does not work. Without a Service Worker there is no way to answer a
  // media element's range requests, and the honest fallback is exactly what this card did before:
  // decrypt the file and save it. A sentence explaining a capability the reader never knew about
  // would be an apology for nothing.
  if (props.media !== '' && (await startPlaying(key))) return;

  written.value = 0;
  phase.value = 'working';

  try {
    await run(key);
  } catch {
    phase.value = 'asking';
    error.value = error.value || text().failed;
  }
}

/**
 * The key, kept only while a player is on the screen.
 *
 * <p>It is here so «save a copy» does not ask for the passphrase a second time, and it is dropped
 * the moment the player goes away — see <c>stop()</c>. A <c>CryptoKey</c> from <c>unseal</c> is
 * non-extractable, so what is held is the ability to decrypt rather than anything readable.</p>
 */
const opened = ref<CryptoKey | null>(null);
const streamId = ref('');
const streamUrl = ref('');

async function startPlaying(key: CryptoKey): Promise<boolean> {
  const id = crypto.randomUUID();

  const url = await openStream(id, {
    header: props.header,
    key,

    // The ordinary public address. The worker reads ciphertext from it a segment at a time, so the
    // owner's traffic is spent on what is actually watched rather than on the whole film.
    source: props.downloadUrl,
    type: props.mimeType,
  });

  if (!url) return false;

  opened.value = key;
  streamId.value = id;
  streamUrl.value = url;
  phase.value = 'playing';

  return true;
}

/**
 * Takes the player down and forgets everything behind it.
 *
 * <p>The rule this feature is built on is that a decrypted file exists for as long as somebody is
 * watching it and no longer, so leaving the stream registered after the player has gone would be
 * the one piece of that promise this code is responsible for, quietly broken.</p>
 */
function stop() {
  if (streamId.value) closeStream(streamId.value);

  streamId.value = '';
  streamUrl.value = '';
  opened.value = null;
}

onBeforeUnmount(stop);

/** «Save a copy», from the player, without asking for the passphrase again. */
async function saveCopy() {
  const key = opened.value;
  if (!key) return;

  written.value = 0;
  phase.value = 'working';

  try {
    await run(key);
  } catch {
    phase.value = 'playing';
    error.value = error.value || text().failed;
  }
}

async function run(key: CryptoKey) {
  const save = await openSink();

  if (!save) {
    phase.value = 'asking';
    error.value = text().cancelled;
    return;
  }

  const response = await fetch(props.downloadUrl);

  if (!response.ok || !response.body) {
    await save.abort();
    phase.value = 'asking';
    error.value = text().failed;
    return;
  }

  const result = await decryptInto(
    response.body,
    key,
    props.header,
    (plain) => save.write(plain),
    (bytes) => (written.value = bytes),
  );

  if (!result.ok) {
    // Thrown away rather than kept: a partial file that stops in the middle of a verified segment is
    // still a file somebody will open, and it is not the one they asked for.
    await save.abort();
    phase.value = 'asking';
    error.value = reasonText(result.reason);
    return;
  }

  await save.finish();
  phase.value = 'done';
}

const reasonText = (reason: DecryptFailure) =>
  reason === 'corrupt' ? text().corrupt : text().truncated;

/** Where the plaintext goes: onto the disk as it arrives, or into memory when that is all there is. */
interface Sink {
  write(plain: Bytes): Promise<void>;
  finish(): Promise<void>;
  abort(): Promise<void>;
}

async function openSink(): Promise<Sink | null> {
  if (canStreamToDisk) {
    const picker = (globalThis as unknown as FilePicker).showSaveFilePicker;

    let handle: FileSystemFileHandle;
    try {
      handle = await picker({ suggestedName: props.fileName });
    } catch {
      // The picker throws when it is dismissed, which is a choice and not a failure.
      return null;
    }

    const stream = await handle.createWritable();

    return {
      write: (plain) => stream.write(plain),
      finish: () => stream.close(),
      // close() would leave a truncated file on disk under the name the visitor chose.
      abort: () => stream.abort().catch(() => undefined),
    };
  }

  const parts: BlobPart[] = [];

  return {
    write: (plain) => {
      parts.push(plain);
      return Promise.resolve();
    },
    finish: () => {
      offer(new Blob(parts, { type: 'application/octet-stream' }));
      return Promise.resolve();
    },
    abort: () => {
      parts.length = 0;
      return Promise.resolve();
    },
  };
}

function offer(blob: Blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');

  link.href = url;
  link.download = props.fileName;
  link.click();

  // Long enough for the browser to have taken the bytes, and not left for the life of the tab: an
  // object URL pins the whole blob in memory until it is revoked.
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

interface FilePicker {
  showSaveFilePicker(options: { suggestedName?: string }): Promise<FileSystemFileHandle>;
}
</script>

<template>
  <div class="unlock">
    <p class="unlock-title">{{ text().locked }}</p>
    <p class="unlock-explain">{{ text().explain }}</p>

    <p v-if="heavy" class="unlock-warning">{{ text().memory }}</p>

    <form v-if="phase === 'asking' || phase === 'unlocking'" class="unlock-form" @submit.prevent="unlock()">
      <label class="field-label" for="unlock-secret">{{ text().label }}</label>
      <input
        id="unlock-secret"
        v-model="secret"
        type="password"
        class="control"
        autocomplete="off"
        :disabled="phase === 'unlocking'"
      />

      <p v-if="error" class="unlock-error">{{ error }}</p>

      <button type="submit" class="btn btn--primary btn--cta" :disabled="phase === 'unlocking'">
        {{ phase === 'unlocking' ? text().checking : text().unlock }}
      </button>
    </form>

    <div v-else-if="phase === 'working'" class="unlock-progress">
      <span class="unlock-explain">{{ text().working }}</span>
      <div
        class="bar"
        role="progressbar"
        :aria-valuenow="Math.round(percent)"
        aria-valuemin="0"
        aria-valuemax="100"
      >
        <span class="bar-fill" :style="{ width: `${percent}%` }"></span>
      </div>
      <span class="mono" dir="ltr">{{ Math.round(percent) }}%</span>
    </div>

    <!--
      The player. Its src is a URL that exists only inside this browser's Service Worker: every
      range the element asks for is answered with plaintext decrypted a segment at a time, so
      seeking works and nothing is ever written to disk.
    -->
    <div v-else-if="phase === 'playing'" class="unlock-player">
      <video
        v-if="media === 'video'"
        class="unlock-media"
        :src="streamUrl"
        controls
        playsinline
        preload="metadata"
      ></video>
      <audio v-else class="unlock-media" :src="streamUrl" controls preload="metadata"></audio>

      <p class="unlock-explain">{{ text().streaming }}</p>

      <div class="unlock-actions">
        <button type="button" class="btn" @click="saveCopy()">{{ text().saveCopy }}</button>
      </div>
    </div>

    <div v-else class="unlock-progress">
      <p class="unlock-done">{{ text().done }}</p>
      <button type="button" class="btn" @click="phase = streamUrl ? 'playing' : 'asking'">
        {{ streamUrl ? text().play : text().again }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.unlock {
  display: flex;
  flex-direction: column;
  gap: 10px;
  text-align: start;
}

.unlock-title {
  margin: 0;
  font-size: 14px;
  font-weight: 700;
}

.unlock-explain {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.8;
  color: var(--muted);
}

.unlock-warning {
  margin: 0;
  font-size: 12px;
  line-height: 1.8;
  color: var(--warn);
}

.unlock-form,
.unlock-progress {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.unlock-error {
  margin: 0;
  font-size: 12px;
  color: var(--danger);
}

.unlock-done {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--accent-ink);
}

.unlock-player {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

/*
 * The element fills the card and keeps whatever shape the file has.
 *
 * A fixed height would letterbox a portrait video — which is most video shot on the phones this
 * feature exists for — and `height: auto` on an <audio> collapses it to nothing, so the two are
 * given the same rule and the audio player is told to keep the height its controls need.
 */
.unlock-media {
  inline-size: 100%;
  max-block-size: 60vh;
  border-radius: 10px;
  background: var(--surface2);
}

audio.unlock-media {
  block-size: 40px;
  max-block-size: none;
}

.unlock-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
</style>
