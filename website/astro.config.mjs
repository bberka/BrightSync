import { defineConfig } from 'astro/config';
import sitemap from '@astrojs/sitemap';

// Custom domain: https://brightsync.bbay.dev/ (CNAME in public/, DNS CNAME to bberka.github.io)
export default defineConfig({
  site: 'https://brightsync.bbay.dev',
  trailingSlash: 'always',
  integrations: [sitemap()],
  build: { assets: 'assets' },
});
