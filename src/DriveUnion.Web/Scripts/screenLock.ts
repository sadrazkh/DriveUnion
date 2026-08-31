/**
 * Keeping the screen awake while a transfer is running.
 *
 * <p>A phone dims, then sleeps, and a sleeping phone suspends the page — which stops an upload and
 * stops a download to the device. The wake lock is the only thing a page can do about that, and it
 * is worth doing even though it is imperfect: it holds only while the app is in front, and the
 * browser takes it away the moment the page is hidden.</p>
 *
 * <p><b>The lock is per return-to-visible, not once.</b> Because the browser revokes it on hide, a
 * lock taken at the start of a two-hour download is gone the first time the customer answers a
 * message. Every path here is therefore written to be called again on every wake.</p>
 *
 * <p>Safari has had it since 16.4, so every path is also allowed to do nothing. A refusal — an older
 * phone, a battery-saver policy, a document that went hidden mid-request — is not a failure of the
 * transfer and is not reported as one.</p>
 *
 * <p>This was the upload queue's, inline, and is shared because keeping a film downloading needs
 * exactly the same thing for exactly the same reasons. Two copies of this reasoning would be two
 * things to keep correct, and the second would be the one nobody read.</p>
 */
export interface ScreenLock {
  /** Takes it, if there is anything to hold it for. Safe to call on every wake. */
  readonly take: () => Promise<void>;

  /** Gives it back. Safe to call when there is none. */
  readonly release: () => void;
}

/**
 * @param wanted Whether a lock is still worth holding — asked before requesting and again after,
 * because the transfer may have finished while the request was in flight and a lock nobody is
 * waiting on is a screen that never dims again.
 */
export function createScreenLock(wanted: () => boolean): ScreenLock {
  let sentinel: WakeLockSentinel | null = null;
  let asking = false;

  async function take(): Promise<void> {
    // The browser takes the lock away by itself when the page is hidden, and hands back a sentinel
    // that says so rather than a null. Forgetting a spent one is what makes the next return to
    // visible ask again instead of holding a reference to a lock that stopped existing.
    if (sentinel?.released === true) sentinel = null;

    // Asked on every return to visible, including the ones where nothing is moving. A lock taken
    // then is a screen that will not dim with nothing being transferred.
    if (!wanted() || sentinel !== null || asking) return;

    if (!('wakeLock' in navigator)) return;

    // request() rejects outright on a hidden document, so this is not a precaution — it is the
    // difference between asking and throwing.
    if (document.visibilityState !== 'visible') return;

    asking = true;

    try {
      const held = await navigator.wakeLock.request('screen');

      if (!wanted()) {
        void held.release().catch(() => undefined);
        return;
      }

      sentinel = held;
    } catch {
      // Refused, or unavailable. Either way the transfer is unaffected and the screen behaves as it
      // did before this feature existed.
    } finally {
      asking = false;
    }
  }

  function release(): void {
    const held = sentinel;
    if (held === null) return;

    sentinel = null;
    void held.release().catch(() => undefined);
  }

  return { take, release };
}
