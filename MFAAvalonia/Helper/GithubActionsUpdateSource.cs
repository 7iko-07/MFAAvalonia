using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

internal sealed record GithubActionsArtifact(long Id, string Name, string Digest);
internal sealed record GithubActionsDelta(GithubActionsArtifact Artifact, string Asset, string Sha256, long Size);

// The supplied client must disable automatic redirects. Credentials are used only
// on api.github.com; temporary artifact-storage URLs are followed without them.
internal sealed class GithubActionsUpdateSource(HttpClient client, string token, TimeSpan? operationTimeout = null)
{
    private CancellationTokenSource CreateDeadline(CancellationToken cancellation, TimeSpan defaultTimeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(operationTimeout ?? defaultTimeout);
        return deadline;
    }

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";

    private static string NormalizePlatform(string platform) =>
        platform.Replace("macos-", "osx-").Replace("-x86_64", "-x64").Replace("-aarch64", "-arm64");

    private static bool Available(JsonElement artifact) =>
        artifact.TryGetProperty("expired", out var expired) && expired.ValueKind == JsonValueKind.False;

    private static GithubActionsArtifact Artifact(JsonElement value) =>
        new(value.GetProperty("id").GetInt64(), Text(value, "name"), Text(value, "digest"));

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool authenticated, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("MFAComponentUpdater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (authenticated)
        {
            if (uri.Scheme != "https" || uri.Host != "api.github.com") throw new InvalidDataException("无效的 GitHub API 地址。");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
    }

    private async Task<JsonElement> JsonAsync(string url, CancellationToken cancellation)
    {
        using var response = await SendAsync(new Uri(url), true, cancellation);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        return document.RootElement.Clone();
    }

    public async Task<GithubActionsDelta?> FindDeltaAsync(string owner, string repository, string version,
        string baseline, string fullName, string fullSha256, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(token) || fullSha256.Length != 64 || !fullSha256.All(Uri.IsHexDigit)) return null;
        // One deadline covers the complete lookup, including JSON bodies,
        // pagination, redirects and metadata reads after response headers arrive.
        using var deadline = CreateDeadline(cancellation, TimeSpan.FromMinutes(1));
        cancellation = deadline.Token;
        var identity = Regex.Match(fullName, @"-(win|linux|macos|osx)-(x86_64|x64|arm64|aarch64)(?:-|\.)");
        if (!identity.Success) return null;
        var platform = NormalizePlatform(identity.Groups[1].Value + "-" + identity.Groups[2].Value);
        var fullRepository = owner + "/" + repository;
        var api = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
        var tag = await JsonAsync($"{api}/git/ref/tags/{Uri.EscapeDataString(version)}", cancellation);
        var target = tag.GetProperty("object");
        for (var depth = 0; Text(target, "type") == "tag" && depth < 8; depth++)
            target = (await JsonAsync($"{api}/git/tags/{Uri.EscapeDataString(Text(target, "sha"))}", cancellation)).GetProperty("object");
        if (Text(target, "type") != "commit") throw new InvalidDataException("无法解析发布标签。");
        var sha = Text(target, "sha");
        var metadataName = "update-metadata-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version)))[..24].ToLowerInvariant();
        for (var page = 1; ; page++)
        {
            var result = await JsonAsync($"{api}/actions/artifacts?name={metadataName}&per_page=100&page={page}", cancellation);
            var artifacts = result.GetProperty("artifacts");
            foreach (var metadata in artifacts.EnumerateArray())
            {
                if (Text(metadata, "name") != metadataName || !Available(metadata)
                    || !metadata.TryGetProperty("workflow_run", out var origin) || Text(origin, "head_sha") != sha) continue;
                var runId = origin.GetProperty("id").GetInt64();
                var run = await JsonAsync($"{api}/actions/runs/{runId}", cancellation);
                if (Text(run, "status") != "completed" || Text(run, "conclusion") != "success"
                    || Text(run, "event") is not ("push" or "workflow_dispatch")
                    || Text(run, "head_branch") != version || Text(run, "head_sha") != sha
                    || Text(run, "path").Split('@')[0] != ".github/workflows/install.yml"
                    || !run.TryGetProperty("head_repository", out var headRepository)
                    || !Text(headRepository, "full_name").Equals(fullRepository, StringComparison.OrdinalIgnoreCase)) continue;
                var temporary = Path.Combine(Path.GetTempPath(), "mfa-actions-index-" + Guid.NewGuid().ToString("N") + ".zip");
                JsonElement index;
                try
                {
                    await DownloadArtifactAsync(api, Artifact(metadata), temporary, 64 * 1024 * 1024, null, cancellation);
                    using var package = ZipFile.OpenRead(temporary);
                    var entries = package.Entries.Where(e => e.FullName == "update-index.json").ToArray();
                    if (entries.Length != 1 || entries[0].Length > 4 * 1024 * 1024) throw new InvalidDataException("无效的增量索引构件。");
                    using var stream = entries[0].Open();
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
                    index = document.RootElement.Clone();
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                var source = index.GetProperty("source");
                if (index.GetProperty("schema").GetInt32() != 1 || Text(index, "version") != version
                    || !Text(source, "repository").Equals(fullRepository, StringComparison.OrdinalIgnoreCase)
                    || Text(source, "headSha") != sha || Text(source, "runId") != runId.ToString())
                    throw new InvalidDataException("增量索引与目标发布版本不匹配。");
                var matches = index.GetProperty("deltas").EnumerateArray().Where(d => Text(d, "fullAsset") == fullName
                    && Text(d, "fullSha256").Equals(fullSha256, StringComparison.OrdinalIgnoreCase)
                    && Text(d, "baseVersion") == baseline && Text(d, "version") == version
                    && NormalizePlatform(Text(d, "platform")) == platform).ToArray();
                if (matches.Length == 0) return null;
                if (matches.Length != 1) throw new InvalidDataException("增量索引存在重复项。");
                var delta = matches[0];
                var asset = Text(delta, "asset");
                var hash = Text(delta, "sha256");
                var size = delta.GetProperty("size").GetInt64();
                if (!Regex.IsMatch(asset, @"\Adelta-[0-9a-f]{24}\.zip\z") || hash.Length != 64
                    || !hash.All(Uri.IsHexDigit) || size <= 0 || size > long.MaxValue - 1024 * 1024)
                    throw new InvalidDataException("无效的增量包索引。");
                for (var artifactPage = 1; ; artifactPage++)
                {
                    var payloads = (await JsonAsync($"{api}/actions/runs/{runId}/artifacts?per_page=100&page={artifactPage}", cancellation)).GetProperty("artifacts");
                    var payload = payloads.EnumerateArray().FirstOrDefault(a => Text(a, "name") == asset[..^4] && Available(a));
                    if (payload.ValueKind != JsonValueKind.Undefined) return new GithubActionsDelta(Artifact(payload), asset, hash, size);
                    if (payloads.GetArrayLength() < 100) return null;
                }
            }
            if (artifacts.GetArrayLength() < 100) return null;
        }
    }

    public async Task DownloadDeltaAsync(string owner, string repository, GithubActionsDelta delta,
        string destination, Action<double>? progress = null, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Actions 增量下载需要 GitHub Token。");
        // Payloads may be large on slow links. Keep a finite overall deadline
        // across redirects, response-body reads, extraction and verification.
        using var deadline = CreateDeadline(cancellation, TimeSpan.FromMinutes(15));
        cancellation = deadline.Token;
        var api = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
        var wrapper = destination + ".artifact";
        try
        {
            await DownloadArtifactAsync(api, delta.Artifact, wrapper, delta.Size + 1024 * 1024, progress, cancellation);
            using var package = ZipFile.OpenRead(wrapper);
            if (package.Entries.Count != 1 || package.Entries[0].FullName != delta.Asset || package.Entries[0].Length != delta.Size)
                throw new InvalidDataException("增量构件中的文件与索引不匹配。");
            using var input = package.Entries[0].Open();
            await using (var output = File.Create(destination)) await input.CopyToAsync(output, cancellation);
            await using var payload = File.OpenRead(destination);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(payload, cancellation));
            if (!hash.Equals(delta.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("增量包 SHA-256 校验失败。");
        }
        finally { if (File.Exists(wrapper)) File.Delete(wrapper); }
    }

    private async Task DownloadArtifactAsync(string api, GithubActionsArtifact artifact, string destination,
        long maximumBytes, Action<double>? progress, CancellationToken cancellation)
    {
        var uri = new Uri($"{api}/actions/artifacts/{artifact.Id}/zip");
        for (var redirects = 0; redirects < 6; redirects++)
        {
            using var response = await SendAsync(uri, redirects == 0, cancellation);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                uri = response.Headers.Location is { } location ? new Uri(uri, location) : throw new InvalidDataException("构件下载缺少重定向地址。");
                if (uri.Scheme != "https") throw new InvalidDataException("无效的构件下载地址。");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("构件大小超出限制。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = File.Create(destination))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    // HttpClient's timeout ends at response headers in this
                    // mode. Reuse it as an idle-read limit, while the parent
                    // token still enforces the overall operation deadline.
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    readDeadline.CancelAfter(client.Timeout);
                    var count = await input.ReadAsync(buffer, readDeadline.Token);
                    if (count == 0) break;
                    total += count;
                    if (total > maximumBytes) throw new InvalidDataException("构件大小超出限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    if (response.Content.Headers.ContentLength is > 0) progress?.Invoke(total * 100d / response.Content.Headers.ContentLength.Value);
                }
            }
            if (!string.IsNullOrEmpty(artifact.Digest))
            {
                await using var file = File.OpenRead(destination);
                var digest = "sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
                if (!digest.Equals(artifact.Digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Actions 构件校验失败。");
            }
            progress?.Invoke(100);
            return;
        }
        throw new InvalidDataException("构件下载重定向次数过多。");
    }
}
