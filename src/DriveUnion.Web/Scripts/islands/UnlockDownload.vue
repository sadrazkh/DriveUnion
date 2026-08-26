<script setup lang="ts">
import { computed, ref } from 'vue';
import { unseal, type Secret } from '../crypto/envelope';
import { decryptInto, type DecryptFailure } from '../crypto/stream';
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
  lang: 'fa' | 'en';
}>();

type Phase = 'asking' | 'unlocking' | 'working' | 'done';

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

  written.value = 0;
  phase.value = 'working';

  try {
    await run(key);
  } catch {
    phase.value = 'asking';
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

    <div v-else class="unlock-progress">
      <p class="unlock-done">{{ text().done }}</p>
      <button type="button" class="btn" @click="phase = 'asking'">{{ text().again }}</button>
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
</style>
