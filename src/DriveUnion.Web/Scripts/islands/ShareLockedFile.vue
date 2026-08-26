<script setup lang="ts">
import { computed, onMounted, ref, watchEffect } from 'vue';
import { newRecoveryKey, rewrap, unsealForRewrap } from '../crypto/envelope';
import type { EncryptionHeader } from '../crypto/format';

/**
 * Making a link to a locked file, without handing over the passphrase.
 *
 * <p>The whole of what happens here: the owner types the secret their file was uploaded with, this
 * unwraps that one file's content key, wraps it again under a key generated for this link, and puts
 * the three resulting fields into the form. The server gets a second wrapped copy of a 32-byte key
 * and nothing else — no passphrase, no content key, and not a byte of the file, which is untouched
 * on disk and is the same ciphertext both secrets open.</p>
 *
 * <p>It replaces the form's own submit rather than sitting beside it, because the material has to
 * exist before the post and the two secrets must never be in the request. With no JavaScript the
 * form still works and still makes a link — one opened by the owner's passphrase, which is what
 * shipped with the format. The markup below the mount point says so.</p>
 */

const props = defineProps<{
  header: EncryptionHeader;
  fileName: string;
  lang: 'fa' | 'en';
}>();

type Phase = 'asking' | 'working' | 'ready';

const secret = ref('');
const phase = ref<Phase>('asking');
const error = ref('');
const linkKey = ref('');
const kept = ref(false);
const copied = ref(false);

const fa = computed(() => props.lang !== 'en');

/** Filled once the key exists; the form posts it and the server stores it beside the link. */
const material = ref('');

const root = ref<HTMLElement | null>(null);

/**
 * The form's own button, which lives outside this island and has to be governed by it.
 *
 * <p>Razor renders it disabled. Without that, pressing «ساخت لینک» before doing any of this would
 * make a link with no key of its own — one that hands out the file's original wrapped key and so
 * needs the owner's passphrase, which is the exact footgun this island exists to remove. Silent,
 * too: the link would work, for the wrong secret, and the owner would find out from the person they
 * sent it to.</p>
 *
 * <p>Reaching out of the island for one node is not something to do twice, and it is the honest
 * arrangement here: the button belongs to a form that works without script, and this is script
 * deciding when that form is ready.</p>
 */
onMounted(() => {
  const button = root.value
    ?.closest('form')
    ?.querySelector<HTMLButtonElement>('[data-share-submit]');

  if (!button) return;

  watchEffect(() => {
    button.disabled = !(phase.value === 'ready' && kept.value);
  });
});

function text() {
  return fa.value
    ? {
        heading: 'این فایل قفل است',
        explain:
          'برای ساختن لینک باید یک‌بار همین‌جا بازش کنیم تا کلیدِ همین فایل را با رمز تازه‌ای بسته‌بندی کنیم. رمز خودتان جایی فرستاده نمی‌شود و گیرنده هرگز آن را نمی‌بیند.',
        why: 'بدون این، تنها راه اشتراک این بود که رمز خودتان را بدهید — رمزی که هر فایل دیگری هم که با آن آپلود شده را باز می‌کند.',
        yours: 'رمز یا کلید خودتان',
        unlock: 'باز کردن و ساختن کلید لینک',
        working: 'در حال باز کردن…',
        wrong: 'این رمز این فایل را باز نمی‌کند.',
        needSecret: 'رمز خودتان را وارد کنید.',
        made: 'کلید این لینک ساخته شد',
        madeHint:
          'این را همراه لینک به گیرنده بدهید. فقط همین یک فایل را باز می‌کند و با باطل‌کردن لینک از کار می‌افتد.',
        copy: 'کپی',
        copiedIt: 'کپی شد',
        keptIt: 'این کلید را برداشتم',
        onlyOnce: 'این کلید فقط همین یک‌بار نشان داده می‌شود؛ ما نسخه‌ای از آن نگه نمی‌داریم.',
      }
    : {
        heading: 'This file is locked',
        explain:
          'To make a link we have to open it here once, so we can wrap this one file’s key under a new secret. Your own passphrase is never sent anywhere and the recipient never sees it.',
        why: 'Without this, the only way to share it was to give out your own passphrase — which opens every other file uploaded with it.',
        yours: 'Your passphrase or key',
        unlock: 'Unlock and make a key for this link',
        working: 'Opening…',
        wrong: 'That does not open this file.',
        needSecret: 'Enter your passphrase or key.',
        made: 'This link has its own key',
        madeHint:
          'Give this to the recipient along with the link. It opens this one file and stops working when the link is revoked.',
        copy: 'Copy',
        copiedIt: 'Copied',
        keptIt: 'I have taken this key',
        onlyOnce: 'Shown once, here. We keep no copy of it.',
      };
}

async function make() {
  if (secret.value.length === 0) {
    error.value = text().needSecret;
    return;
  }

  error.value = '';
  phase.value = 'working';

  // Extractable, because wrapKey has to read the key — see envelope.ts, which says why that is a
  // separate function from the one the download path uses.
  const key = await unsealForRewrap({ kind: 'passphrase', value: secret.value }, props.header);

  if (!key) {
    phase.value = 'asking';
    error.value = text().wrong;
    return;
  }

  linkKey.value = newRecoveryKey();
  material.value = JSON.stringify(
    await rewrap(key, { kind: 'recoveryKey', value: linkKey.value }));

  // The owner's secret has done its one job. Cleared so it is not sitting in a field on a screen
  // somebody may walk away from, and so nothing can submit it by accident.
  secret.value = '';
  phase.value = 'ready';
}

async function copyKey() {
  try {
    await navigator.clipboard.writeText(linkKey.value);
    copied.value = true;
    setTimeout(() => (copied.value = false), 1600);
  } catch {
    // Clipboard refused or an insecure origin. The field is selectable either way.
  }
}
</script>

<template>
  <div ref="root" class="sharelock">
    <p class="sharelock-heading">🔒 {{ text().heading }}</p>

    <template v-if="phase !== 'ready'">
      <p class="sharelock-note">{{ text().explain }}</p>
      <p class="sharelock-note">{{ text().why }}</p>

      <label class="field-label" for="share-secret">{{ text().yours }}</label>
      <!--
        Deliberately outside the posted fields: it has no `name`, so even a stray submit cannot
        carry it. What the form posts is the hidden input below, which is a wrapped key.
      -->
      <input
        id="share-secret"
        v-model="secret"
        type="password"
        class="control"
        autocomplete="off"
        :disabled="phase === 'working'"
      />

      <p v-if="error" class="sharelock-error">{{ error }}</p>

      <button
        type="button"
        class="btn btn--block"
        :disabled="phase === 'working'"
        @click="make()"
      >{{ phase === 'working' ? text().working : text().unlock }}</button>
    </template>

    <template v-else>
      <p class="sharelock-made">{{ text().made }}</p>
      <p class="sharelock-note">{{ text().madeHint }}</p>

      <!-- dir="ltr": base64url in groups of six, which RTL would otherwise rearrange. -->
      <div class="field" dir="ltr">
        <input
          class="control mono sharelock-key"
          readonly
          :value="linkKey"
          :aria-label="text().made"
          @focus="($event.target as HTMLInputElement).select()"
        />
        <button type="button" class="btn btn--sm" @click="copyKey()">
          {{ copied ? text().copiedIt : text().copy }}
        </button>
      </div>

      <p class="sharelock-warning">{{ text().onlyOnce }}</p>

      <label class="upload-check">
        <input type="checkbox" v-model="kept" />
        <span>{{ text().keptIt }}</span>
      </label>

      <!-- The only thing here that is posted. -->
      <input type="hidden" name="key" :value="material" />
    </template>
  </div>
</template>

<style scoped>
.sharelock {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  margin-bottom: 12px;
  border: 1px solid var(--line);
  border-radius: 11px;
  background: var(--surface2);
}

.sharelock-heading {
  margin: 0;
  font-size: 12.5px;
  font-weight: 700;
}

.sharelock-made {
  margin: 0;
  font-size: 12.5px;
  font-weight: 700;
  color: var(--accent-ink);
}

.sharelock-note {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.8;
  color: var(--muted);
}

.sharelock-warning {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.8;
  color: var(--warn);
}

.sharelock-error {
  margin: 0;
  font-size: 11.5px;
  color: var(--danger);
}

.sharelock-key {
  flex: 1;
  letter-spacing: 0.04em;
}
</style>
