import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The UI calls /api/*; Vite forwards those to Fleet.Api on port 5100.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5100',
    },
  },
});
