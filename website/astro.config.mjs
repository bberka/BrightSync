import { defineConfig } from 'astro/config';
import sitemap from '@astrojs/sitemap';

// Project site: https://bberka.github.io/BrightSync/
export default defineConfig({
  site: 'https://bberka.github.io',
  base: '/BrightSync',
  trailingSlash: 'always',
  integrations: [sitemap()],
  build: { assets: 'assets' },
});
