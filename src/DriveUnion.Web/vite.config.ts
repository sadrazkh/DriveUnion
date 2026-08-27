import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'node:path';

// Vite compiles the Vue islands straight into wwwroot/build with a manifest. Razor reads that
// manifest (Infrastructure/ViteManifest.cs) to reference the hashed assets. This is the whole
// "embedded Vue" story, ported from Harbora — no standalone Node server runs in production.
export default defineConfig({
  plugins: [vue()],
  base: '/build/',
  build: {
    // Write the manifest at build/manifest.json, NOT Vite's default build/.vite/manifest.json:
    // the .NET SDK excludes dot-folders from `dotnet publish`, so the hidden location silently
    // drops the manifest out of the published image and the app comes up with no CSS at all.
    // Do not "tidy" this back to the default.
    manifest: 'manifest.json',
    outDir: resolve(import.meta.dirname, 'wwwroot/build'),
    emptyOutDir: true,
    rollupOptions: {
      // Relative input → stable manifest key "Scripts/main.ts", which is what ViteManifest
      // resolves by. An absolute path here would key the manifest by this machine's directory.
      //
      // One entry, and the service worker is deliberately not a second one.
      //
      // A worker's scope is the directory it is served from, so anything emitted here would be
      // scoped to /build/ and could control the stylesheets and none of the pages. `entryFileNames`
      // could be made to write '../sw.js' out of `outDir`, and that is where the idea stops being
      // merely awkward and becomes wrong: two entries make Rollup emit ES modules and hoist what
      // they share into a third chunk, and iOS has no module service worker — `type: 'module'` is
      // Chromium-only. On the one platform the PWA work exists for, that worker fails to parse and
      // there is no worker at all, with nothing raised anywhere. Sharing a chunk with main.ts is
      // the second half of the same trap: one transitive import that touches `document` throws
      // during install and loses the worker the same silent way.
      //
      // So the worker is hand-written at wwwroot/sw.js and served straight from the root, next to
      // the other hand-written source under wwwroot — css/app.css, css/tokens.css. Its address is
      // stable across deploys, which is what lets a browser recognise an update to it instead of
      // installing a second worker beside it. The full argument is at the top of that file; this
      // note is here because this is where somebody would go to "tidy" it into the bundle.
      input: 'Scripts/main.ts',
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    cors: true,
  },
});
