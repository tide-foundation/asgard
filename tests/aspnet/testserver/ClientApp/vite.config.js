import { defineConfig } from "vite";
import path from "path";

// Builds straight into the server's wwwroot. emptyOutDir wipes it each build —
// safe here because the frontend adaptor is served from the adaptor directory,
// not from wwwroot.
export default defineConfig({
  resolve: {
    symlinks: false,
  },
  build: {
    outDir: path.resolve(__dirname, "../wwwroot"),
    emptyOutDir: true,
  },
});
