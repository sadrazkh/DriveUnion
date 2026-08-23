// Copies Vazirmatn out of node_modules into wwwroot/fonts.
//
// The handoff's `@import` points at cdn.jsdelivr.net. That cannot ship: the server is in Germany
// and the handoff forbids a foreign CDN, so every panel visit would otherwise open a connection to
// a third party, hand it the visitor's IP and Referer, and make first paint depend on a host we do
// not run. The font is a build input, not a runtime dependency.
//
// Runs from `prebuild`, so `npm run build` always refreshes the copies from the pinned package
// version and the provenance of the bytes under wwwroot/fonts is never a guess.
import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'wwwroot');
const source = join(webRoot, '..', 'node_modules', 'vazirmatn', 'fonts', 'webfonts');
const target = join(webRoot, 'fonts');

// The variable font, not the nine static cuts. The design reaches for 400/500/600/700/800 on
// almost every screen; five static files are ~255 kB against one 111 kB variable file that covers
// the whole axis. Variable-font support predates every browser this panel targets.
//
// Renamed on the way out: the package ships it as `Vazirmatn[wght].woff2`, and square brackets in
// a URL have to be percent-encoded — a detail that survives exactly until someone hand-writes the
// path in a stylesheet.
const files = [['Vazirmatn[wght].woff2', 'Vazirmatn-Variable.woff2']];

mkdirSync(target, { recursive: true });
for (const [from, to] of files) {
  copyFileSync(join(source, from), join(target, to));
  console.log(`fonts: ${from} -> wwwroot/fonts/${to}`);
}
