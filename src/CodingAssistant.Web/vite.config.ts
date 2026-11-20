import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  server: {
    host: '127.0.0.1',
    strictPort: true,
    proxy: { '/api': 'http://127.0.0.1:5098' },
  },
  build: {
    outDir: fileURLToPath(new URL('../CodingAssistant.Api/wwwroot/', import.meta.url)),
    emptyOutDir: true,
    sourcemap: false,
  },
});
