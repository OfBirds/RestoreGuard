using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.DbDump;

/// <summary>Lists the logical-DB dump directory (Tier 3) on the backup host over SSH.</summary>
public sealed class DbDumpProvider(ISshProvider ssh)
{
    public async Task<IReadOnlyList<BackupArtifact>> GetArtifactsAsync(
        string hostAlias, string path, string sqlMethod = "pg_dumpall", CancellationToken ct = default)
    {
        var quoted = "'" + path.Replace("'", "'\\''") + "'";
        var result = await ssh.RunAsync(
            hostAlias,
            $"find {quoted} -maxdepth 1 -type f -printf '%f\\t%s\\t%T@\\n'",
            ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"listing {path} on {hostAlias} failed: {result.StdErr.Trim()}");

        return DumpListingParser.Parse(result.StdOut, $"{hostAlias}:{path}", sqlMethod);
    }
}
