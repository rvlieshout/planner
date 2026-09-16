# Third-party notices

This client redistributes the following. All three are compiled into the bundle and served from this
application's own origin rather than fetched from a CDN, which is what makes these notices necessary.

## Lucide

<https://lucide.dev> — ISC License, Copyright (c) 2022 Lucide Contributors. Lucide is a fork of
Feather Icons, Copyright (c) 2013–2022 Cole Bemis.

The icons this application uses are vendored, as path data, into `src/lib/icons/icons.ts` by
`scripts/build-icons.mjs`. The full license text ships with the `lucide-static` package in
`node_modules/lucide-static/LICENSE`.

## Inter

<https://rsms.me/inter/> — SIL Open Font License 1.1, Copyright (c) 2016 The Inter Project Authors.

Shipped through `@fontsource-variable/inter`; the license text is in
`node_modules/@fontsource-variable/inter/LICENSE`. Only the latin and latin-ext subsets are included
in the build.

## JetBrains Mono

<https://www.jetbrains.com/lp/mono/> — SIL Open Font License 1.1, Copyright (c) 2020 The JetBrains
Mono Project Authors.

Shipped through `@fontsource/jetbrains-mono`; the license text is in
`node_modules/@fontsource/jetbrains-mono/LICENSE`. Only the latin and latin-ext subsets, at weights
400 and 500, are included in the build.

---

The build and framework dependencies — SvelteKit, Svelte, Vite and `@microsoft/signalr` — are MIT
licensed and their notices travel with their packages.
