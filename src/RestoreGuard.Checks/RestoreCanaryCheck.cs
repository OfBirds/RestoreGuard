using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

/// <summary>
/// The check the product is named for: snapshots existing is not the same as
/// backups restoring. Each probed source streamed its configured canary file out
/// of the LATEST snapshot; 0 bytes back means the restore failed (bad passphrase,
/// corrupt chunks, path no longer in the backup) or the file is empty — either
/// way the backup cannot be trusted to restore, which is RED by definition.
/// Probe results are injected (the PbsMaintenanceCheck pattern): rules stay pure.
/// </summary>
public sealed class RestoreCanaryCheck(IReadOnlyList<CanaryResult> results) : ICheck
{
    public string RuleId => "restore-canary";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        foreach (var r in results)
        {
            if (r.Bytes > 0)
                continue; // the canary restored — this source has proven it can restore

            var why = r.Detail is null
                ? "the restore produced no output and the tool reported nothing"
                : $"the tool said: {r.Detail}";
            yield return new Finding(
                "restore-canary/failed", Severity.Red, r.SourceName, r.Host,
                $"Restoring canary '{r.CanaryPath}' from the latest snapshot returned 0 bytes — {why}.",
                "The backup exists but did not restore. Check the canary path is still included in the backup, "
                + "the password/passphrase file is the right one, and run the tool's own check command on the repo.");
        }
    }
}
