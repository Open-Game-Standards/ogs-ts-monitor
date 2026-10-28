namespace Ogs.Telemetry.Monitor.Tests;

/// <summary>The standard's conformance corpus: OGS_TS_CONFORMANCE if set,
/// else the sibling checkout (Code/Open-Game-Standards/OGS-Telemetry-Standard,
/// which CI reproduces).</summary>
static class Corpus
{
    static readonly Lazy<string> Dir = new(Find);

    public static byte[] Datagram(string id) =>
        Convert.FromHexString(string.Concat(
            File.ReadAllText(Path.Combine(Dir.Value, "vectors", id + ".hex")).Where(c => !char.IsWhiteSpace(c))));

    static string Find()
    {
        var env = Environment.GetEnvironmentVariable("OGS_TS_CONFORMANCE");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "manifest.json"))) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "OGS-Telemetry-Standard", "conformance");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new InvalidOperationException(
            "OGS-TS conformance corpus not found: check out OGS-Telemetry-Standard beside this repo or set OGS_TS_CONFORMANCE");
    }
}
