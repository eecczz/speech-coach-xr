import { defineConfig } from 'vite';
import { resolve } from 'node:path';

export default defineConfig({
  root: '.',
  build: {
    outDir: '../../services/audio-pipeline/app/static',
    emptyOutDir: true,
    rollupOptions: {
      input: {
        index: resolve(__dirname, 'index.html'),
        createProject: resolve(__dirname, 'create-project.html'),
        practiceSituation: resolve(__dirname, 'practice-situation.html'),
        focusSelection: resolve(__dirname, 'focus-selection.html'),
        loading: resolve(__dirname, 'loading.html'),
        practice: resolve(__dirname, 'practice.html'),
        report: resolve(__dirname, 'report.html'),
        dashboard: resolve(__dirname, 'dashboard.html'),
        interviewXr: resolve(__dirname, 'interview-xr.html'),
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/ws/stt': {
        target: 'ws://localhost:8000',
        ws: true,
        changeOrigin: true,
      },
      '/ws/signals': {
        target: 'ws://localhost:8001',
        ws: true,
        changeOrigin: true,
      },
      '/ws/hud': {
        target: 'ws://localhost:8001',
        ws: true,
        changeOrigin: true,
      },
      // aggregator REST — must be proxied or fetch() lands on the SPA's index.html
      '/session': {
        target: 'http://localhost:8001',
        changeOrigin: true,
      },
      // audio-pipeline batch analysis at session end (webm → STT + prosody)
      '/analyze': {
        target: 'http://localhost:8000',
        changeOrigin: true,
      },
      '/api/coach': {
        target: 'http://localhost:8002',
        changeOrigin: true,
        // Strip the /api/coach prefix so calls to /api/coach/agent-feedback land
        // on coach's /agent-feedback route, not /api/coach/agent-feedback.
        rewrite: (path) => path.replace(/^\/api\/coach/, ''),
      },
      // audio-pipeline app-api routes: /api/auth/*, /api/sessions/*, /api/health/db
      '/api': {
        target: 'http://localhost:8000',
        changeOrigin: true,
      },
    },
  },
});
