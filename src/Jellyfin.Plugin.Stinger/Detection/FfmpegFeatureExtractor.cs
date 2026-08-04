using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Stinger.Detection;

/// <summary>
/// Runs Jellyfin's bundled ffmpeg over the tail of a file and parses per-frame
/// signalstats/scene/loudness metadata into a <see cref="FeatureSeries"/>.
/// </summary>
public partial class FfmpegFeatureExtractor
{
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(10);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FfmpegFeatureExtractor> _logger;

    public FfmpegFeatureExtractor(IMediaEncoder mediaEncoder, ILogger<FfmpegFeatureExtractor> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public async Task<FeatureSeries> ExtractAsync(
        string mediaPath, TimeSpan runtime, TimeSpan tailWindow, CancellationToken cancellationToken)
    {
        var duration = runtime.TotalSeconds;
        var tailSeconds = Math.Min(tailWindow.TotalSeconds, duration * 0.4);
        var tailStart = Math.Max(0, duration - tailSeconds);

        var workDir = Directory.CreateTempSubdirectory("jellyfin-stinger-").FullName;
        try
        {
            var videoLog = "video.log";
            var audioLog = "audio.log";

            await RunFfmpegAsync(
                new[]
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin",
                    "-ss", tailStart.ToString("F2", CultureInfo.InvariantCulture),
                    "-i", mediaPath,
                    "-map", "0:v:0",
                    "-vf", $"fps=2,scale=160:-2,select=gte(scene\\,0),signalstats,metadata=mode=print:file={videoLog}",
                    "-an", "-sn",
                    "-f", "null", "-",
                },
                workDir,
                required: true,
                cancellationToken).ConfigureAwait(false);

            await RunFfmpegAsync(
                new[]
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin",
                    "-ss", tailStart.ToString("F2", CultureInfo.InvariantCulture),
                    "-i", mediaPath,
                    "-map", "0:a:0?",
                    "-af", $"ebur128=metadata=1,ametadata=mode=print:key=lavfi.r128.M:file={audioLog}",
                    "-vn", "-sn",
                    "-f", "null", "-",
                },
                workDir,
                required: false,
                cancellationToken).ConfigureAwait(false);

            var video = ParseVideoLog(Path.Combine(workDir, videoLog), tailStart);
            var audio = ParseAudioLog(Path.Combine(workDir, audioLog), tailStart);

            return new FeatureSeries
            {
                TailStart = tailStart,
                Duration = duration,
                Video = video,
                Audio = audio,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task RunFfmpegAsync(
        string[] args, string workDir, bool required, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FfmpegTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            if (required)
            {
                throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {Truncate(stderr)}");
            }

            _logger.LogDebug("Optional ffmpeg pass failed ({Code}): {Stderr}", process.ExitCode, Truncate(stderr));
        }
    }

    private static List<VideoFrame> ParseVideoLog(string path, double tailStart)
    {
        var frames = new List<VideoFrame>();
        if (!File.Exists(path))
        {
            return frames;
        }

        double time = 0, yavg = 0, satavg = 0, ydif = 0, scene = 0;
        var haveFrame = false;

        void Flush()
        {
            if (haveFrame)
            {
                frames.Add(new VideoFrame(tailStart + time, yavg, satavg, ydif, scene));
            }
        }

        foreach (var line in File.ReadLines(path))
        {
            var frameMatch = PtsTimeRegex().Match(line);
            if (frameMatch.Success)
            {
                Flush();
                time = double.Parse(frameMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                yavg = satavg = ydif = scene = 0;
                haveFrame = true;
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0 || !double.TryParse(line[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            switch (line[..eq])
            {
                case "lavfi.signalstats.YAVG": yavg = value; break;
                case "lavfi.signalstats.SATAVG": satavg = value; break;
                case "lavfi.signalstats.YDIF": ydif = value; break;
                case "lavfi.scene_score": scene = value; break;
            }
        }

        Flush();
        return frames;
    }

    private static List<AudioFrame> ParseAudioLog(string path, double tailStart)
    {
        var frames = new List<AudioFrame>();
        if (!File.Exists(path))
        {
            return frames;
        }

        double time = 0;
        var haveTime = false;
        foreach (var line in File.ReadLines(path))
        {
            var frameMatch = PtsTimeRegex().Match(line);
            if (frameMatch.Success)
            {
                time = double.Parse(frameMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                haveTime = true;
                continue;
            }

            if (haveTime && line.StartsWith("lavfi.r128.M=", StringComparison.Ordinal)
                && double.TryParse(line["lavfi.r128.M=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs))
            {
                frames.Add(new AudioFrame(tailStart + time, lufs));
            }
        }

        return frames;
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    [GeneratedRegex(@"pts_time:\s*([0-9.eE+-]+)")]
    private static partial Regex PtsTimeRegex();
}
