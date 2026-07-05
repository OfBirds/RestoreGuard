using System.Globalization;
using System.Text.RegularExpressions;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Providers.DbDump;

/// <summary>
/// Parses `find -printf '%f\t%s\t%T@\n'` output of a logical-DB dump directory.
/// Filename contract (db-backup-prod.sh): &lt;container-name&gt;_&lt;yyyyMMdd&gt;.sql.gz,
/// plus app-secrets_&lt;yyyyMMdd&gt;.tar.gz for the secrets sweep.
/// </summary>
public static partial class DumpListingParser
{
    [GeneratedRegex(@"^(?<target>.+)_(?<date>\d{8})\.(?<ext>sql\.gz|tar\.gz)$")]
    private static partial Regex DumpFileName();

    public static IReadOnlyList<BackupArtifact> Parse(string listing, string locationPrefix, string sqlMethod = "pg_dumpall")
    {
        var artifacts = new List<BackupArtifact>();

        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length != 3)
                continue;

            var fileName = parts[0];
            if (!long.TryParse(parts[1], out var size))
                continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch))
                continue;

            var m = DumpFileName().Match(fileName);
            if (!m.Success)
                continue;

            var isSqlDump = m.Groups["ext"].Value == "sql.gz";
            artifacts.Add(new BackupArtifact(
                Tier: BackupTier.LogicalDb,
                TargetService: m.Groups["target"].Value,
                Location: $"{locationPrefix}/{fileName}",
                Timestamp: DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000)),
                SizeBytes: size,
                Method: isSqlDump ? sqlMethod : "tar",
                HasOffsiteCopy: false));
        }

        return artifacts;
    }
}
