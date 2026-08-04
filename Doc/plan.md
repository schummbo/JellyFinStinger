# JellyFinStinger — Plan

Jellyfin plugin that detects and labels movie **stingers** (mid-credits / post-credits scenes) — primarily by analyzing the video itself, with external sources as a cross-check.

## Decisions

| Decision | Choice |
|---|---|
| Target | Jellyfin 10.10+, C#/.NET plugin |
| Primary strategy | **Local detection** (ffmpeg analysis of the credits region) |
| External sources | TMDB stinger keywords + Wikipedia list, as validation signal only. No AfterCredits scraping (fragile, ToS-gray) — revisit if needed. |
| Labeling model | **Three-state**: HasStinger / NoStinger (affirmative only) / Unknown |
| Surfacing | 1) Media segment markers, 2) overview/metadata text, 3) playback notification |

### Why local detection

- Media segment markers need a **timestamp**; no external database has timestamps. Only local analysis can say *where* the stinger is.
- Works for any file, including obscure/foreign/older titles no database covers.
- External sources still answer *whether* a stinger exists cheaply — agreement with detection raises confidence, disagreement demotes to Unknown.

## Detection algorithm

Analyze only the tail of the film (last ~15 min or last 20%, whichever is smaller), decoded at low fps (~2–4) and scaled down (~320px wide). Uses Jellyfin's bundled ffmpeg (`IMediaEncoder` exposes the path).

**Feature extraction (per sampled frame / audio window):**
- Brightness + luma histogram (`signalstats`) — credits are mostly dark
- Frame-difference / motion — credits are low-motion or uniform vertical scroll
- Scene-change score (`select=gt(scene,N)`)
- Black-frame runs (`blackdetect`)
- Audio loudness envelope (`ebur128`) — catches audio-only stingers and dialogue after music-only credits

**Classification:**
1. Find the **credits region**: sustained run of low-brightness, low-motion, text-like frames reaching the end of file. (If a chapter marker or existing media segment already marks credits, use it as a prior.)
2. Find **stinger candidates**: contiguous ≥ ~8s regions *inside or after* the credits region with photographic content — higher brightness variance, color, motion, scene changes.
3. Label: candidate bounded by credits on both sides → **mid-credits**; candidate at the tail → **post-credits**; no candidate and clean credits detected → **NoStinger**; ambiguous signal (credits-over-footage, stylized credits) → **Unknown**.
4. Cross-check TMDB keywords (`aftercreditsstinger` 179430 / `duringcreditsstinger` 179431) and cached Wikipedia list:
   - Detection + source agree → confident
   - Detection found one, sources silent → still HasStinger (sources are known-incomplete)
   - Sources say yes, detection found nothing → **Unknown** + log for review (detection likely missed it)

**Known hard cases** (expect Unknown, not wrong answers): credits over live footage (Jackie Chan outtakes), stylized/bright credits, audio-only stingers, files with trailers appended after the movie.

## Plugin architecture

```
Jellyfin.Plugin.Stinger/
├── Plugin.cs                    # plugin entry, config page
├── Configuration/               # thresholds, tail window, enable flags per surface
├── ScheduledTasks/
│   └── StingerScanTask.cs       # iterates movies, runs pipeline, caches results
├── Detection/
│   ├── FfmpegFeatureExtractor.cs  # runs ffmpeg, parses filter output
│   └── StingerClassifier.cs       # pure logic — unit-testable on feature series
├── Sources/
│   ├── TmdbKeywordSource.cs
│   └── WikipediaListSource.cs   # crawl list page → local index, refresh weekly
├── Providers/
│   └── StingerSegmentProvider.cs  # IMediaSegmentProvider
├── Playback/
│   └── StingerNotifier.cs       # ISessionManager playback-progress hook
└── Data/                        # per-item result store (JSON or plugin datastore)
```

**Surfacing details:**
- **Segments**: emit an `Outro` segment for the credits that **ends where the stinger begins** — clients' "skip credits" then lands on the stinger. For post-credits, also consider an `Outro` covering credits-after-stinger.
- **Overview text**: append a marker line to the overview, e.g. `🎬 Mid-credits + post-credits scenes` (idempotent — detect and replace our own line, never duplicate). Configurable off.
- **Notification**: on playback progress near credits start, send a client message via `ISessionManager` ("Stay tuned — scene after the credits"). Configurable off.
- Three-state is stored in plugin data; Unknown gets no visible label by default.

## Distribution (install from GitHub)

Jellyfin installs plugins from a **plugin repository** — a hosted `manifest.json` with version metadata, zip download URLs, and MD5 checksums. Setup:

- Host `manifest.json` on GitHub Pages (or a raw URL on a `repo` branch) in this repo.
- GitHub Actions on tagged release: build, package the plugin zip with **`jprm`** (jellyfin-plugin-repository-manager), attach zip to the GitHub Release, regenerate `manifest.json`.
- One-time user step: add the manifest URL under Dashboard → Plugins → Repositories. Installs and updates then come through the normal catalog UI automatically.

## Milestones

1. **Skeleton** — plugin builds, loads in Jellyfin, config page, empty scheduled task. (Use the official `jellyfin-plugin-template`.)
2. **Feature extractor** — run ffmpeg on a file's tail, parse features into a time series. CLI/test harness to dump the series for eyeballing.
3. **Classifier** — credits + stinger region detection over the series. Unit tests from recorded feature series of known movies (Marvel = has, plus known no-stinger titles).
4. **External cross-check** — TMDB keywords (needs API key in config) + Wikipedia index.
5. **Surfacing** — segment provider, overview text, notification.
6. **Tuning pass** — run across real library, review Unknowns/disagreements, adjust thresholds.
7. **Release pipeline** — GitHub Actions + `jprm` + GitHub Pages manifest; verify install from the repo URL on a clean Jellyfin.

## Open items

- Verify exact `IMediaSegmentProvider` interface shape and segment types in 10.10 (against jellyfin source / plugin docs) at milestone 1.
- Decide result storage: plugin JSON store vs. Jellyfin item provider IDs.
- Whether scan runs automatically on library scan vs. manual/scheduled only (start: scheduled task, manual trigger).
