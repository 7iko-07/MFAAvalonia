using MFAAvalonia.Helper;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ActionsUpdateTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        async Task Reject(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or HttpRequestException) { check(true, message); return; }
            throw new Exception(message);
        }

        using (var fixture = new Fixture(check))
        {
            using var client = new HttpClient(fixture);
            var source = new GithubActionsUpdateSource(client, "");
            check(await source.FindDeltaAsync("owner", "repo", "v2", "v1", Fixture.FullName, fixture.FullHash) == null, "No token selected Actions");
            check(fixture.Requests == 0, "No-token lookup sent an Actions request");
        }
        foreach (var mode in new[] { "success", "annotated", "pagination", "manual" })
        {
            using var fixture = new Fixture(check, mode);
            using var client = new HttpClient(fixture);
            var source = new GithubActionsUpdateSource(client, "test-token");
            var delta = await source.FindDeltaAsync("owner", "repo", "v2", "v1", Fixture.FullName, fixture.FullHash);
            check(delta != null, "Matching Actions delta not found: " + mode);
            var destination = Path.Combine(root, "actions-" + mode + ".zip");
            await source.DownloadDeltaAsync("owner", "repo", delta!, destination);
            check(File.ReadAllBytes(destination).SequenceEqual(fixture.InnerZip), "Incorrect inner ZIP extracted");
            check(fixture.Downloaded.SequenceEqual(new long[] { 1, 2 }), "Downloaded an unrelated platform or baseline");
            check(!File.Exists(destination + ".artifact"), "Artifact wrapper was not cleaned up");
        }
        foreach (var mode in new[] { "expired-index", "expired-delta", "missing-delta", "wrong-commit", "wrong-workflow",
                     "wrong-tag", "wrong-repository", "failed-run", "pr-run", "wrong-baseline", "wrong-platform", "wrong-full-hash" })
        {
            using var fixture = new Fixture(check, mode);
            using var client = new HttpClient(fixture);
            var source = new GithubActionsUpdateSource(client, "test-token");
            check(await source.FindDeltaAsync("owner", "repo", "v2", "v1", Fixture.FullName, fixture.FullHash) == null, "Unsafe/unavailable delta selected: " + mode);
        }
        foreach (var mode in new[] { "unauthorized", "forbidden", "expired-download", "index-digest", "wrong-version", "wrong-source" })
        {
            using var fixture = new Fixture(check, mode);
            using var client = new HttpClient(fixture);
            var source = new GithubActionsUpdateSource(client, "test-token");
            await Reject(async () => { await source.FindDeltaAsync("owner", "repo", "v2", "v1", Fixture.FullName, fixture.FullHash); }, "Invalid lookup accepted: " + mode);
        }
        foreach (var mode in new[] { "delta-digest", "payload-hash", "traversal", "extra-file", "delta-gone" })
        {
            using var fixture = new Fixture(check, mode);
            using var client = new HttpClient(fixture);
            var source = new GithubActionsUpdateSource(client, "test-token");
            var delta = await source.FindDeltaAsync("owner", "repo", "v2", "v1", Fixture.FullName, fixture.FullHash);
            var destination = Path.Combine(root, "actions-" + mode + ".zip");
            await Reject(() => source.DownloadDeltaAsync("owner", "repo", delta!, destination), "Invalid payload accepted: " + mode);
            check(!File.Exists(destination + ".artifact"), "Failed download retained wrapper");
        }
        foreach (var mode in new[] { "stalled-api", "stalled-metadata", "stalled-payload", "slow-lookup" })
        {
            using var fixture = new Fixture(check, mode);
            using var client = new HttpClient(fixture) { Timeout = TimeSpan.FromSeconds(1) };
            var source = new GithubActionsUpdateSource(client, "test-token", operationTimeout: TimeSpan.FromMilliseconds(150));
            var destination = Path.Combine(root, "actions-" + mode + ".zip");
            var timedOut = false;
            try
            {
                var delta = await source.FindDeltaAsync("owner", "repo", "v2", "v1", Fixture.FullName, fixture.FullHash)
                    .WaitAsync(TimeSpan.FromSeconds(3));
                if (mode == "stalled-payload")
                    await source.DownloadDeltaAsync("owner", "repo", delta!, destination).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException) { timedOut = true; }
            check(timedOut, "Stalled Actions operation did not reach its own deadline: " + mode);
            check(!File.Exists(destination + ".artifact"), "Timed-out download retained wrapper");
            if (mode == "slow-lookup") check(fixture.Requests >= 2, "Lookup deadline was not shared across multiple requests");
            // Lookup/download failures are caught by VersionChecker and select
            // the existing Release full-package path before installation begins.
        }
        using (var fixture = new Fixture(check, "stalled-payload"))
        using (var client = new HttpClient(fixture) { Timeout = TimeSpan.FromMilliseconds(100) })
        {
            // Leave the production overall deadlines intact. A stalled body
            // must honor the much shorter configured idle/network timeout.
            var source = new GithubActionsUpdateSource(client, "test-token");
            var delta = await source.FindDeltaAsync("owner", "repo", "v2", "v1", Fixture.FullName, fixture.FullHash);
            var destination = Path.Combine(root, "actions-idle-timeout.zip");
            var timedOut = false;
            try { await source.DownloadDeltaAsync("owner", "repo", delta!, destination).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { timedOut = true; }
            check(timedOut, "Stalled payload ignored the configured idle timeout");
            check(!File.Exists(destination + ".artifact"), "Idle-timeout download retained wrapper");
        }
        if (Environment.GetEnvironmentVariable("MFA_ACTIONS_FIXTURE") is { Length: > 0 } producerRoot)
        {
            using var fixture = new Fixture(check, producerRoot: producerRoot);
            using var client = new HttpClient(fixture);
            var source = new GithubActionsUpdateSource(client, "test-token");
            var delta = await source.FindDeltaAsync("owner", "repo", "v2", "v1", Fixture.FullName, fixture.FullHash);
            check(delta != null, "Python producer metadata not understood by client");
            var destination = Path.Combine(root, "actions-producer.zip");
            await source.DownloadDeltaAsync("owner", "repo", delta!, destination);
            check(File.ReadAllBytes(destination).SequenceEqual(fixture.InnerZip), "Python-produced delta changed in transport");
            var extracted = Path.Combine(root, "actions-producer");
            ZipFile.ExtractToDirectory(destination, extracted);
            var manifest = MFAUpdater.UpdateManifest.Read(Path.Combine(extracted, MFAUpdater.UpdateManifest.FileName));
            check(manifest.Kind == "delta" && manifest.Version == "v2" && manifest.BaseVersion == "v1", "Transport lost producer versions");
            foreach (var file in manifest.Files.Where(file => manifest.Payload.Contains(file.Path)))
                check(file.Matches(Path.Combine(extracted, file.Path)), "Transport lost producer file data: " + file.Path);
        }
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public const string FullName = "MaaShiningNikki-win-x86_64-v2.zip";
        private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly string deltaName = "delta-bbbbbbbbbbbbbbbbbbbbbbbb.zip";
        private readonly Action<bool, string> check;
        private readonly string mode;
        private readonly byte[] metadata;
        private readonly byte[] wrapper;
        private readonly string metadataName = "update-metadata-" + Hash(Encoding.UTF8.GetBytes("v2"))[..24];
        public readonly byte[] InnerZip = Zip(new Dictionary<string, byte[]> { ["update-manifest.json"] = Encoding.UTF8.GetBytes("{}") });
        public readonly string FullHash = new('c', 64);
        public readonly List<long> Downloaded = [];
        public int Requests;

        public Fixture(Action<bool, string> check, string mode = "success", string? producerRoot = null)
        {
            this.check = check;
            this.mode = mode;
            if (producerRoot != null)
            {
                var metadataRoot = Path.Combine(producerRoot, "metadata");
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(metadataRoot, "update-index.json")));
                var delta = document.RootElement.GetProperty("deltas")[0];
                deltaName = delta.GetProperty("asset").GetString()!;
                FullHash = delta.GetProperty("fullSha256").GetString()!;
                InnerZip = File.ReadAllBytes(Path.Combine(producerRoot, "deltas", deltaName));
                metadata = Zip(Directory.GetFiles(metadataRoot).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes));
                wrapper = Zip(new Dictionary<string, byte[]> { [deltaName] = InnerZip });
                return;
            }
            var index = new
            {
                schema = 1, version = mode == "wrong-version" ? "v3" : "v2",
                source = new { repository = "owner/repo", headSha = Sha, runId = mode == "wrong-source" ? "999" : "42" },
                deltas = new[] { new { fullAsset = FullName, asset = deltaName,
                    baseVersion = mode == "wrong-baseline" ? "v0" : "v1", version = "v2",
                    platform = mode == "wrong-platform" ? "linux-x86_64" : "win-x86_64",
                    sha256 = mode == "payload-hash" ? new string('0', 64) : Hash(InnerZip), size = InnerZip.Length,
                    fullSha256 = mode == "wrong-full-hash" ? new string('0', 64) : FullHash } }
            };
            metadata = Zip(new Dictionary<string, byte[]> { ["update-index.json"] = JsonSerializer.SerializeToUtf8Bytes(index) });
            var files = new Dictionary<string, byte[]> { [mode == "traversal" ? "../" + deltaName : deltaName] = InnerZip };
            if (mode == "extra-file") files["extra"] = [];
            wrapper = Zip(files);
        }

        private object Artifact(long id, bool expired = false) => new
        {
            id, name = id == 1 ? metadataName : deltaName[..^4], expired,
            digest = "sha256:" + (mode == (id == 1 ? "index-digest" : "delta-digest") ? new string('0', 64) : Hash(id == 1 ? metadata : wrapper)),
            workflow_run = new { id = 42, head_sha = mode == "wrong-commit" ? "other" : Sha }
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests++;
            var uri = request.RequestUri!;
            if (mode == "slow-lookup") await Task.Delay(60, cancellation);
            var authenticated = uri.Host == "api.github.com";
            check(request.Headers.Authorization?.ToString() == (authenticated ? "Bearer test-token" : null), "Token leaked or was omitted: " + uri.Host);
            var path = uri.AbsolutePath;
            HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value)) };
            HttpResponseMessage response;
            if (mode == "stalled-api" || (uri.Host == "storage.example" &&
                ((mode == "stalled-metadata" && path == "/1") || (mode == "stalled-payload" && path == "/2"))))
                response = new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
            else if (mode == "unauthorized") response = new(HttpStatusCode.Unauthorized);
            else if (mode == "forbidden") response = new(HttpStatusCode.Forbidden);
            else if (uri.Host == "storage.example") response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(path == "/1" ? metadata : wrapper) };
            else if (path.EndsWith("/git/ref/tags/v2")) response = Json(new { @object = new { type = mode == "annotated" ? "tag" : "commit", sha = Sha } });
            else if (path.EndsWith("/git/tags/" + Sha)) response = Json(new { @object = new { type = "commit", sha = Sha } });
            else if (path.EndsWith("/actions/artifacts"))
            {
                var items = new[] { Artifact(1, mode == "expired-index") };
                // Query places page last; distinguish page=1 from per_page=100.
                if (mode == "pagination" && uri.Query.EndsWith("&page=1")) items = Enumerable.Repeat<object>(new { name = "other" }, 100).ToArray();
                response = Json(new { artifacts = items });
            }
            else if (path.EndsWith("/actions/runs/42")) response = Json(new
            {
                status = "completed", conclusion = mode == "failed-run" ? "failure" : "success",
                @event = mode == "pr-run" ? "pull_request" : mode == "manual" ? "workflow_dispatch" : "push", head_branch = mode == "wrong-tag" ? "main" : "v2", head_sha = Sha,
                path = mode == "wrong-workflow" ? ".github/workflows/test.yml" : ".github/workflows/install.yml",
                head_repository = new { full_name = mode == "wrong-repository" ? "fork/repo" : "owner/repo" }
            });
            else if (path.EndsWith("/actions/runs/42/artifacts"))
            {
                var items = mode == "missing-delta" ? Array.Empty<object>() : new[] { Artifact(2, mode == "expired-delta") };
                if (mode == "pagination" && uri.Query.EndsWith("&page=1")) items = Enumerable.Repeat<object>(new { name = "other" }, 100).ToArray();
                response = Json(new { artifacts = items });
            }
            else if (path.EndsWith("/zip"))
            {
                var id = long.Parse(path.Split('/')[^2]);
                Downloaded.Add(id);
                response = mode == "expired-download" || (mode == "delta-gone" && id == 2) ? new(HttpStatusCode.Gone) : new(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://storage.example/" + id);
            }
            else throw new Exception("Unexpected Actions request: " + uri);
            return response;
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        private static byte[] Zip(Dictionary<string, byte[]> files)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                foreach (var (name, bytes) in files)
                {
                    using var stream = zip.CreateEntry(name).Open();
                    stream.Write(bytes);
                }
            return buffer.ToArray();
        }
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
