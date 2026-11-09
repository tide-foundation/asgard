import { defineConfig } from "vite";

export default defineConfig({
  resolve: {
    symlinks: false, // tidecloak-js is linked from a local checkout
  },
  build: {
    outDir: "../WebApp.Server/wwwroot", // the server's web root
    emptyOutDir: true,
  },
});
