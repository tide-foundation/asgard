import { defineConfig } from "vite";
import path from "path";

export default defineConfig({
  resolve: {
    symlinks: false,
  },
  build: {
    outDir: path.resolve(__dirname, "../wwwroot"),
    emptyOutDir: true,
  },
});
