import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  resolve: {
    symlinks: false, // tidecloak-js is linked from a local checkout
  },
  build: {
    outDir: "../LockingUserDataWithTide.Server/wwwroot", // the server's web root
    emptyOutDir: true,
  },
});
