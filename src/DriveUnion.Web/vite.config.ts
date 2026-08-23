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
      input: 'Scripts/main.ts',
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    cors: true,
  },
});
