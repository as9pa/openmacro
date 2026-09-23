using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenMacro.Engine;

namespace OpenMacro.App;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(
    Version Version,
    string HtmlUrl,
    IReadOnlyList<ReleaseAsset> Assets
)
{
    public string Label => $"v{ReleaseHelpers.Format(Version)}";
}

public enum UpdateCheckOutcome
{
    UpToDate,
    Available,

    // 404, offline, rate-limited, unreadable: only a manual check says so.
    Unavailable,
}

/// <summary>
/// Reads the latest GitHub release, downloads its single-file exe on request,
/// and swaps it in for the running one. Unauthenticated; the ETag is kept in
/// memory so the hourly re-check is a free 304 when nothing changed.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string LatestUrl = "https://api.github.com/repos/as9pa/openmacro/releases/latest";
    private const string SumsAssetName = "SHA256SUMS";

    /// <summary>Passed to the exe started by <see cref="ApplyAndRestart"/>:
    /// it waits for this instance to exit instead of refusing to run.</summary>
    public const string RestartArg = "--after-update";

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string? etag;
    private ReleaseInfo? cached;

    public UpdateService()
    {
        // GitHub's API refuses requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("openmacro", ReleaseHelpers.Format(CurrentVersion))
        );
    }

    public static Version CurrentVersion =>
        ReleaseHelpers.Normalize(
            Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0)
        );

    public static string CurrentLabel => $"v{ReleaseHelpers.Format(CurrentVersion)}";

    /// <summary>True for the self-contained single-file exe, the only build
    /// that can replace itself. Its runtime is bundled, so even CoreLib has
    /// no path on disk; the framework-dependent zip (and a dev build) loads
    /// CoreLib from an installed runtime and gets the release page instead.</summary>
#pragma warning disable IL3000 // the empty single-file Location is the signal
    public static bool CanSelfUpdate =>
        string.IsNullOrEmpty(typeof(object).Assembly.Location)
        && Environment.ProcessPath is not null;
#pragma warning restore IL3000

    public static string UpdatesFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "openmacro",
            "updates"
        );

    /// <summary>Asks GitHub for the latest release. Never throws: anything
    /// that goes wrong is <see cref="UpdateCheckOutcome.Unavailable"/>.</summary>
    public async Task<(UpdateCheckOutcome Outcome, ReleaseInfo? Release)> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json")
            );
            if (etag is not null && cached is not null)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await http.SendAsync(request);
            ReleaseInfo? release;
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                release = cached;
            }
            else
            {
                if (!response.IsSuccessStatusCode)
                    return (UpdateCheckOutcome.Unavailable, null);

                release = Parse(await response.Content.ReadAsStringAsync());
                if (release is null)
                    return (UpdateCheckOutcome.Unavailable, null);

                cached = release;
                etag = response.Headers.ETag?.ToString();
            }

            return ReleaseHelpers.IsNewer(release.Version, CurrentVersion)
                ? (UpdateCheckOutcome.Available, release)
                : (UpdateCheckOutcome.UpToDate, release);
        }
        catch (Exception e)
            when (
                e
                    is HttpRequestException
                        or TaskCanceledException
                        or JsonException
                        // a field of the wrong JSON type
                        or InvalidOperationException
            )
        {
            return (UpdateCheckOutcome.Unavailable, null);
        }
    }

    private static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (
            !root.TryGetProperty("tag_name", out var tag)
            || ReleaseHelpers.ParseTag(tag.GetString()) is not { } version
        )
            return null;

        var htmlUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u)
                    ? u.GetString()
                    : null;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var l) ? l : 0;
                if (name is not null && url is not null)
                    assets.Add(new ReleaseAsset(name, url, size));
            }
        }

        return new ReleaseInfo(version, htmlUrl, assets);
    }

    /// <summary>Downloads the release's exe into <see cref="UpdatesFolder"/>,
    /// reporting whole percents, and checks it against the release's
    /// SHA256SUMS. Returns the file's path; throws (and leaves no file
    /// behind) on any failure, a hash mismatch included.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<int> progress)
    {
        var exe =
            release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ) ?? throw new InvalidOperationException("The release has no exe.");
        var sumsAsset =
            release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, SumsAssetName, StringComparison.OrdinalIgnoreCase)
            ) ?? throw new InvalidOperationException("The release has no SHA256SUMS.");

        var sums = ReleaseHelpers.ParseSha256Sums(await http.GetStringAsync(sumsAsset.DownloadUrl));
        if (!sums.TryGetValue(exe.Name, out var expected))
            throw new InvalidOperationException("SHA256SUMS doesn't list the exe.");

        Directory.CreateDirectory(UpdatesFolder);
        var path = Path.Combine(UpdatesFolder, Path.GetFileName(exe.Name));
        try
        {
            using (
                var response = await http.GetAsync(
                    exe.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead
                )
            )
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? exe.Size;

                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(path);
                var buffer = new byte[81920];
                long done = 0;
                var reported = -1;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    var percent = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                    if (percent != reported)
                    {
                        reported = percent;
                        progress.Report(percent);
                    }
                }
            }

            string actual;
            await using (var file = File.OpenRead(path))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file));

            if (actual != expected)
                throw new InvalidOperationException("The download's SHA-256 doesn't match.");

            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Swaps <paramref name="downloaded"/> in for the running exe and
    /// starts it: the running exe becomes "&lt;exe&gt;.old" (Windows lets a
    /// running exe be renamed, not deleted), the download takes its name, and
    /// the new exe is started. Any failure puts both files back where they
    /// were and throws; the caller shuts down only on success.</summary>
    public static void ApplyAndRestart(string downloaded)
    {
        var exe =
            Environment.ProcessPath
            ?? throw new InvalidOperationException("The running exe has no path.");
        var old = exe + ".old";

        if (File.Exists(old))
            File.Delete(old); // a leftover the launch cleanup couldn't take
        File.Move(exe, old);

        var swapped = false;
        try
        {
            File.Move(downloaded, exe);
            swapped = true;
            Process.Start(
                new ProcessStartInfo(exe, RestartArg)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                }
            );
        }
        catch
        {
            if (swapped)
                File.Move(exe, downloaded); // keep the download for a Retry
            File.Move(old, exe);
            throw;
        }
    }

    /// <summary>Deletes the "&lt;exe&gt;.old" a previous update left beside
    /// the running exe. The old process may still be exiting when the new one
    /// starts, so a failed delete is retried a few times in the background;
    /// if it never succeeds, the next launch tries again.</summary>
    public static void DeleteLeftoverOldExe()
    {
        if (Environment.ProcessPath is not { } exe)
            return;

        var old = exe + ".old";
        if (!File.Exists(old) || TryDelete(old))
            return;

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (TryDelete(old))
                    return;
            }
        });
    }

    /// <summary>Opens the release's GitHub page in the default browser.</summary>
    public static void OpenReleasePage(ReleaseInfo release)
    {
        try
        {
            Process.Start(new ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
        }
        catch
        {
            // No browser to hand it to: nothing more to do.
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => http.Dispose();
}
