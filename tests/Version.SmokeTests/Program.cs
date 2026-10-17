using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MFAAvalonia.Helper;
using Semver;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}

foreach (var (input, expected) in new (string?, string)[]
{
    ("2.12.12", "v2.12.12"),
    ("2.12.12+commit", "v2.12.12"),
    ("v2.12.13-beta.2+commit", "v2.12.13-beta.2"),
    (" V2.12.13-rc.1 ", "v2.12.13-rc.1"),
    ("2.12.0-dev", "v2.12.0-dev"),
    (null, "v0.0.0-dev"),
    ("", "v0.0.0-dev"),
    ("invalid", "v0.0.0-dev"),
    ("2.12.13-beta.02", "v0.0.0-dev"),
})
    Check(AppVersion.FromInformationalVersion(input) == expected, $"Incorrect application version for '{input}'");

Check(AppVersion.Current == "v9.8.7-beta.2", "Version did not come from assembly informational metadata");

bool UpdateAvailable(string latest, string installed) =>
    SemVersion.Parse(latest[1..]).ComparePrecedenceTo(SemVersion.Parse(installed[1..])) > 0;
var installed = AppVersion.FromInformationalVersion("2.12.12+installed-commit");
Check(!UpdateAvailable("v2.12.12", installed), "Same release requested another update");
Check(UpdateAvailable("v2.12.13", installed), "Newer release not offered");
Check(!UpdateAvailable("v2.12.11", installed), "Older release offered");
Check(UpdateAvailable("v2.12.13", AppVersion.FromInformationalVersion("2.12.13-beta.2+commit")), "Stable release not offered after beta");
Check(!UpdateAvailable("v2.12.13-beta.2", AppVersion.FromInformationalVersion("2.12.13")), "Beta offered after stable");
Check(!UpdateAvailable("v2.12.12+different-commit", installed), "Build metadata caused an update");

if (args.Length != 0)
{
    if (args.Length != 3 || args[0] != "--verify")
        throw new ArgumentException("Usage: Version.SmokeTests [--verify <published Core DLL> <release tag>]");

    var tag = args[2];
    if (!tag.StartsWith('v')) throw new InvalidDataException("Release tag must start with v");
    SemVersion.Parse(tag[1..]);
    using var stream = File.OpenRead(args[1]);
    using var pe = new PEReader(stream);
    var reader = pe.GetMetadataReader();
    string? informationalVersion = null;
    foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
    {
        var attribute = reader.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
        var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
        var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        if (reader.GetString(type.Namespace) != "System.Reflection"
            || reader.GetString(type.Name) != "AssemblyInformationalVersionAttribute") continue;
        var blob = reader.GetBlobReader(attribute.Value);
        if (blob.ReadUInt16() != 1) throw new InvalidDataException("Invalid custom attribute header");
        informationalVersion = blob.ReadSerializedString();
    }

    Check(informationalVersion != null, "Published assembly has no informational version");
    // Permit the SDK's commit suffix, but require the complete release version (including prerelease).
    Check(informationalVersion == tag[1..] || informationalVersion!.StartsWith(tag[1..] + "+", StringComparison.Ordinal),
        $"Published version '{informationalVersion}' does not match release tag '{tag}'");
    Check(AppVersion.FromInformationalVersion(informationalVersion) == AppVersion.FromInformationalVersion(tag),
        "Runtime version does not match release tag");
    Console.WriteLine($"Verified {args[1]}: {informationalVersion} ({tag})");
}

Console.WriteLine($"Version checks passed: {passed}");
