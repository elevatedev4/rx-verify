# Rx Verify — Setup.exe installer channel

An ADDITIONAL way to install/update Rx Verify, alongside the existing
`bootstrap-fresh.ps1` + Desktop shortcut flow described in `README.md` —
that flow keeps working exactly as it does today, unchanged, on any PC
that keeps using it. This is unsigned for now (private-S3, no code-signing
cert yet).

## First install (per PC)

1. Node.js must already be installed on the PC (major version 20+) — the
   old shortcut's `bootstrap-fresh.ps1` flow installs it via `winget`; this
   installer does **not** bundle Node.js itself, only the overlay app, the
   compiled engine (`current\dist\`), and its drug/NDC/RxNorm datasets
   (`current\data\*.json.gz`) — the loaders in `src/drug/` read `data/` as
   a sibling of `dist/`, so both must land side by side under
   `%LocalAppData%\RxVerifyOverlay\current\` exactly as they sit at the
   repo root in the checkout flow.
2. Download `RxVerifyOverlay-Setup.exe` (link provided separately — the S3
   bucket is private, so there is no public URL to paste here).
3. Run it. Windows SmartScreen will show an "unrecognized app" warning
   (unsigned) — click **More info**, then **Run anyway**. This only
   happens once per PC, on the very first install.
4. The app installs to `%LocalAppData%\RxVerifyOverlay` and launches
   itself.

After that first run, updates are checked for automatically at startup and
applied silently the next time the app launches — no SmartScreen prompt
again, no action needed.

## The old shortcut still works — but never run both at once

If a PC already has the old `bootstrap-fresh.ps1` + Desktop-shortcut
("Rx Verify") install, it keeps working exactly as before. Do **not** run
both the Setup.exe-installed copy and the old shortcut at the same time on
the same PC — a single-instance lock (mutex) means the second one to start
will just show "Rx Verify is already running" and exit; it won't corrupt
anything, but it also won't do anything useful. Pick one per PC.

Both share the exact same `%AppData%\RxVerifyOverlay\settings.json` —
switching from one install method to the other on the same PC carries
your report key and settings over with no migration step.

## Cutting a release

From a normal checkout, on any machine with `git`:

```
git tag v1.0.0
git push --tags
```

Pushing a `v*` tag triggers `.github/workflows/desktop-release.yml`, which
builds the engine (`npm ci && npm run build`), publishes and packs the
overlay together with the committed `data/*.json.gz` datasets (`vpk`), and
uploads the installer + update feed to the private S3 bucket. You can also
trigger it manually from the GitHub Actions tab ("Desktop release" → Run
workflow) with a version number, for a release with no tag.

## One-time repo setup (Will only)

Before the first release, add these under **GitHub → repo Settings →
Secrets and variables → Actions → New repository secret**:

- `DESKTOP_RELEASE_UPLOAD_KEY_ID` — AWS access key id (upload-only IAM
  user, `s3:PutObject`/`s3:ListBucket` on the release bucket)
- `DESKTOP_RELEASE_UPLOAD_SECRET` — that key's secret
- `DESKTOP_RELEASE_BUCKET` — the bucket name (e.g.
  `elevatedev4-desktop-releases`, no `s3://` prefix)

These are separate from the read-only key embedded in the app itself
(`overlay/RxVerifyOverlay/Velopack/release-source.json`, never committed —
see that folder's `release-source.example.json` for the shape) — the CI
secrets can write, the embedded key can only read.
