/**
 * The «کپی» button beside a share link.
 *
 * Not a Vue island, even though it is reached through the same `data-island` registry: the element
 * is already the finished button, server-rendered with its own label. Mounting an app here would
 * replace that markup with a copy of itself for no gain, and would take the button away from anyone
 * whose bundle has not arrived yet.
 */
export function mountCopyLink(el: HTMLElement): void {
  const value = el.dataset.value;
  if (!value) return;

  const original = el.textContent ?? '';
  const copied = document.documentElement.lang === 'en' ? 'Copied' : 'کپی شد';
  let restore = 0;

  el.addEventListener('click', () => {
    void write(value).then((ok) => {
      if (!ok) return;

      el.textContent = copied;
      window.clearTimeout(restore);
      restore = window.setTimeout(() => {
        el.textContent = original;
      }, 1600);
    });
  });
}

/**
 * `navigator.clipboard` needs a secure context. The panel is served over https and localhost counts
 * as one, so the fallback is for the case nobody plans: a deployment reached by IP or over plain
 * http, where the modern call is simply absent and a silent no-op would look like a broken button.
 */
async function write(value: string): Promise<boolean> {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(value);
      return true;
    } catch {
      // Denied by permission or by the context. Fall through rather than give up.
    }
  }

  const scratch = document.createElement('textarea');
  scratch.value = value;
  scratch.setAttribute('readonly', '');
  scratch.style.position = 'fixed';
  scratch.style.insetInlineStart = '-9999px';
  document.body.appendChild(scratch);

  try {
    scratch.select();
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    scratch.remove();
  }
}
