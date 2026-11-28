import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const here = path.dirname(fileURLToPath(import.meta.url))
// repo-root/shared-js  (this file lives in repo-root/src/GarticClone.Client)
const sharedDir = path.resolve(here, '../../shared-js')

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@shared': sharedDir },
  },
  server: {
    // allow Vite to serve files from outside the client folder
    fs: { allow: [path.resolve(here, '../..')] },
  },
})