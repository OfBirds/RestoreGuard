using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.S3;

namespace RestoreGuard.Checks;

/// <summary>What the configured bucket must satisfy. The same expectation type carries
/// provider failure details so connectivity problems are loud but do not stop other rules.</summary>
public sealed record S3BucketExpectation(
    string Name,
    bool ObjectLockRequired,
    int? MinRetentionDays,
    bool CheckNewestObject,
    double MaxNewestObjectAgeHours,
    string? ProviderError = null)
{
    public string Service => $"{Name} bucket";
}

/// <summary>Deterministic immutability rules over bucket metadata already collected by the provider.</summary>
public sealed class S3ImmutabilityCheck(
    IReadOnlyList<S3BucketAudit> audits,
    IReadOnlyList<S3BucketExpectation> expectations) : ICheck
{
    public string RuleId => "s3";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        foreach (var expected in expectations)
        {
            if (expected.ProviderError is { } error)
            {
                yield return new Finding(
                    "s3/unreachable", Severity.Red, expected.Service, expected.Name,
                    $"Bucket '{expected.Name}' metadata could not be read: {error}",
                    "Run `restoreguard --doctor` to distinguish endpoint, credentials, permissions, and bucket-name problems.");
                continue;
            }

            var audit = audits.FirstOrDefault(a => a.Name == expected.Name);
            if (audit is null)
            {
                yield return new Finding(
                    "s3/unreachable", Severity.Red, expected.Service, expected.Name,
                    $"No provider result was returned for bucket '{expected.Name}'.",
                    "Run `restoreguard --doctor`; if configuration just changed, re-run the audit.");
                continue;
            }

            if (!audit.Versioning.VersioningEnabled)
            {
                yield return new Finding(
                    "s3/versioning-disabled", Severity.Red, expected.Service, expected.Name,
                    $"Bucket '{audit.Bucket}' versioning is {audit.Versioning.Status ?? "not configured"}; an old backup can overwrite the current object.",
                    "Enable bucket versioning before relying on this bucket for backups.");
            }

            if (audit.ObjectLock is { } lockState)
            {
                if (expected.ObjectLockRequired && !lockState.Enabled)
                {
                    yield return new Finding(
                        "s3/object-lock-disabled", Severity.Red, expected.Service, expected.Name,
                        $"Object Lock is not enabled on bucket '{audit.Bucket}'.",
                        "Enable Object Lock where the provider and bucket support it, then set a default retention rule; otherwise remove objectLockRequired.");
                }
                else if (lockState.Enabled && !lockState.HasDefaultRetention)
                {
                    yield return new Finding(
                        "s3/object-lock-no-default-retention", Severity.Red, expected.Service, expected.Name,
                        $"Bucket '{audit.Bucket}' has Object Lock enabled but no default retention rule.",
                        "Set a default compliance or governance retention rule so new backups cannot be immediately deleted.");
                }

                var retentionDays = lockState.RetentionDays;
                if (expected.MinRetentionDays is { } minimum &&
                    (retentionDays is null || retentionDays < minimum))
                {
                    yield return new Finding(
                        "s3/object-lock-retention-too-short", Severity.Red, expected.Service, expected.Name,
                        $"Bucket '{audit.Bucket}' default retention is {retentionDays?.ToString() ?? "unknown"} day(s), below the required {minimum}.",
                        $"Raise the default retention rule to at least {minimum} day(s).");
                }
            }

            if (expected.CheckNewestObject)
            {
                if (audit.NewestObjectListingIncomplete)
                {
                    yield return new Finding(
                        "s3/newest-object-unproven", Severity.Yellow, expected.Service, expected.Name,
                        $"The bounded prefix listing for '{audit.Bucket}' stopped before reaching every object, so the newest backup could not be proven fresh.",
                        "Increase maxListRequests/maxListKeys, narrow prefix, or widen the accepted age until the latest object is covered.");
                }
                else if (audit.NewestObject is null)
                {
                    yield return new Finding(
                        "s3/newest-object-missing", Severity.Red, expected.Service, expected.Name,
                        $"No object was found under the configured prefix in bucket '{audit.Bucket}'.",
                        "Verify the backup job and object prefix used by this bucket configuration.");
                }
                else if (inventory.CapturedAt - audit.NewestObject.LastModified > TimeSpan.FromHours(expected.MaxNewestObjectAgeHours))
                {
                    yield return new Finding(
                        "s3/newest-object-stale", Severity.Red, expected.Service, expected.Name,
                        $"Newest object '{audit.NewestObject.Key}' in bucket '{audit.Bucket}' is {inventory.CapturedAt - audit.NewestObject.LastModified:dd\\.hh\\:mm} old, above {expected.MaxNewestObjectAgeHours:0.#}h.",
                        "Check the backup job that writes to this bucket and its endpoint credentials.");
                }
            }
        }
    }
}
