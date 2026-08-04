# Jellyfin Stinger Plugin

Labels movies with whether they have a **stinger** — a mid-credits or post-credits scene — by analyzing the video itself, cross-checked against TMDB keywords and Wikipedia's post-credits list.

## What it does

- **Detects stingers locally.** Samples the last ~15 minutes of each movie with Jellyfin's bundled ffmpeg (brightness, saturation, motion, scene cuts, loudness) and finds islands of photographic content inside or after the closing credits — including audio-only stingers over black.
- **Media segments.** Writes `Outro` segments covering credits-only stretches, each ending where a stinger begins — so a client's *skip credits* button lands **on** the stinger instead of skipping past it.
- **Overview marker.** Appends a line like `🎬 Mid- and post-credits scenes` to the movie overview (idempotent, removable, optional).
- **Playback notification.** Sends a "stay tuned" message to the playing client when the credits start (optional).
- **Three-state results.** *Has stinger* / *no stinger* (affirmed by clean detection) / *unknown* (stylized credits, credits over footage, source disagreement). External sources can affirm presence but never absence — if detection says no and TMDB/Wikipedia say yes, the movie is marked unknown rather than wrong.

## Install

1. In Jellyfin: **Dashboard → Plugins → Repositories → +** and add:
   `https://raw.githubusercontent.com/OWNER/JellyFinStinger/main/manifest.json`
   *(replace `OWNER` with the GitHub user hosting this repo)*
2. Install **Stinger** from the plugin catalog and restart Jellyfin.
3. Run the **Scan movies for stingers** scheduled task (it also runs daily on its own).

Requires Jellyfin **10.10+**.

## Configuration

**Dashboard → Plugins → Stinger**

| Setting | Default | Notes |
|---|---|---|
| TMDB API key | empty | Optional cross-check via the `duringcreditsstinger`/`aftercreditsstinger` keywords |
| Wikipedia cross-check | on | List crawled into a local index, auto-refreshed weekly, kept on failure |
| Media segments | on | The skip-credits-lands-on-stinger behavior |
| Overview marker | on | `🎬 …` line appended to overviews |
| Stay-tuned notification | on | Message shown when playback reaches the credits |
| Tail window | 15 min | How much of the end of each file is analyzed |
| Force rescan | off | One-shot: next scan re-analyzes everything |

## How detection works

Credits are visually distinctive: sustained runs of dark, desaturated, low-motion frames reaching the end of the file. The classifier finds that region, then looks for contiguous ≥8s islands of full-motion photographic content inside it (mid-credits) or at the tail (post-credits), plus active audio over a black tail (audio-only stingers). Ambiguous signatures — credits over footage, stylized credits — come back *unknown*, never a false "no". Results are cached in `data/stinger/results.json` and movies are only re-analyzed when the file changes.

## Development

```
dotnet build src/Jellyfin.Plugin.Stinger
dotnet test tests/Jellyfin.Plugin.Stinger.Tests
```

The classifier is pure logic over a feature time series (`Detection/StingerClassifier.cs`) — unit-testable without ffmpeg or a Jellyfin server.

## Releasing

Push a four-part tag (Jellyfin versions need four parts):

```
git tag v0.1.0.0 && git push origin v0.1.0.0
```

GitHub Actions then tests, packages the plugin with [jprm](https://github.com/oddstr13/jellyfin-plugin-repository-manager), attaches the zip to a GitHub Release, and commits an updated `manifest.json` to `main`. Servers with the repository added pick up the update automatically.
