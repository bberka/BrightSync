# BrightSync website

Static landing page for BrightSync, built with [Astro](https://astro.build) and published to GitHub Pages at
<https://bberka.github.io/BrightSync/>. It ships no client framework: one small script on the download section picks
the right asset from the latest GitHub release, and the page works without it.

```bash
cd website
npm ci
npm run dev        # http://localhost:4321/BrightSync/
npm run build      # astro check (type-check) then astro build into dist/
npm run preview    # serve dist/ exactly as Pages will
```

Requires Node 22.12 or newer.

## Layout

| Path | Purpose |
|---|---|
| `src/pages/` | `index.astro` (the landing page) and `404.astro` |
| `src/layouts/Base.astro` | `<head>`, SEO and social tags, skip link |
| `src/components/` | One component per section (Hero, Features, Platforms, Download, ...) plus inline `Icon` |
| `src/data/site.ts` | Repository URLs, feature list, FAQ, and the `url()` helper that applies the Pages base path |
| `src/styles/global.css` | All styling, dark theme matching the app |
| `src/assets/` | Images that Astro optimizes (screenshots are copies of `docs/assets`; refresh both together) |
| `public/` | Files served as-is: favicon, `og.png`, `robots.txt` |

## Deployment

`.github/workflows/website.yml` builds on every pull request that touches `website/` and deploys `main`/`master` pushes.
Enable it once under **Settings > Pages > Build and deployment > Source: GitHub Actions**.

The site lives under `/BrightSync`, so always build links with `url()` from `src/data/site.ts` instead of a bare `/`.
Moving to a custom domain means changing `site` and removing `base` in `astro.config.mjs`.
