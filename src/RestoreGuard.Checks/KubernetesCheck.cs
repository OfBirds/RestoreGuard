using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Kubernetes;

namespace RestoreGuard.Checks;

public sealed record KubernetesExpectation(string Name, string Alias, TimeSpan MaxBackupAge, bool CheckWorkloads = true,
    bool VeleroRequired = true, string? ProviderError = null)
{
    public string ClusterService => $"{Name} cluster";
}

/// <summary>Deterministic Kubernetes posture and Velero backup checks.</summary>
public sealed class KubernetesCheck(
    IReadOnlyList<KubernetesState> states,
    IReadOnlyList<KubernetesExpectation> expectations) : ICheck
{
    public string RuleId => "k8s";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        foreach (var expected in expectations)
        {
            var state = states.FirstOrDefault(s => s.ClusterIdentity == expected.Name);
            if (state is null)
            {
                yield return new Finding("k8s/unreachable", Severity.Red, expected.ClusterService, expected.Name,
                    $"Kubernetes discovery through '{expected.Alias}' did not complete: {expected.ProviderError ?? "no provider result"}",
                    "Run `restoreguard doctor` for this cluster, then fix kubectl reachability or RBAC before trusting the audit.");
                continue;
            }
            var backups = inventory.Backups.Where(b => b.Tier == BackupTier.KubernetesBackup
                    && b.TargetService == $"{expected.Name} velero")
                .OrderByDescending(b => b.Timestamp).ToList();

            if (expected.VeleroRequired)
            {
                if (!state.VeleroInstalled)
                    yield return new Finding("k8s-backup/velero-missing", Severity.Red, expected.ClusterService, expected.Name,
                        $"Cluster '{expected.Name}' has no readable Velero Backup resource type.",
                        "Install Velero or set veleroNamespace to null if this cluster intentionally has no Velero protection.");
                else if (backups.Count == 0)
                    yield return new Finding("k8s-backup/no-backups", Severity.Red, expected.ClusterService, expected.Name,
                        $"Velero is installed for cluster '{expected.Name}', but no Backup resources were found.",
                        "Create a Velero Schedule and run its first backup; also verify that it includes persistent-volume data.");
                else
                {
                    var latestCompleted = backups.Where(b => string.Equals(b.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(b => b.Timestamp).FirstOrDefault();
                    var latestBackup = backups.OrderByDescending(b => b.Timestamp).First();
                    if (string.Equals(latestBackup.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(latestBackup.Status, "PartiallyFailed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(latestBackup.Status, "FailedValidation", StringComparison.OrdinalIgnoreCase))
                        yield return new Finding("k8s-backup/failed", Severity.Red, expected.ClusterService, expected.Name,
                            $"Latest Velero backup is '{latestBackup.Location}' ({latestBackup.Status}).",
                            "Run `velero backup describe` and inspect the backup logs; partial failures often indicate a PVC snapshot or node-agent problem.");
                    if (latestCompleted is null)
                        yield return new Finding("k8s-backup/no-completed", Severity.Red, expected.ClusterService, expected.Name,
                            "Velero Backup resources exist, but none has completed successfully.",
                            "Fix the latest backup failure and verify a Completed backup before treating the cluster as protected.");
                    else if (inventory.CapturedAt - latestCompleted.Timestamp > expected.MaxBackupAge)
                        yield return new Finding("k8s-backup/stale", Severity.Red, expected.ClusterService, expected.Name,
                            $"Latest Completed Velero backup '{latestCompleted.Location}' is {(inventory.CapturedAt - latestCompleted.Timestamp).TotalHours:F0}h old (limit {expected.MaxBackupAge.TotalHours:F0}h).",
                            "Check the Velero Schedule and its last run: velero backup logs <name>.");

                    if (state.Schedules.Count == 0)
                        yield return new Finding("k8s-backup/no-schedule", Severity.Yellow, expected.ClusterService, expected.Name,
                            "Velero backups exist but no Schedule resources were found.",
                            "Create a Velero Schedule so coverage does not depend on a human.");
                    if (latestCompleted is not null)
                    {
                        var protectedClaims = state.PodVolumeBackups
                            .Where(pvb => pvb.BackupName == latestCompleted.Location && pvb.Completed && pvb.PersistentVolumeClaim is not null)
                            .Select(pvb => $"{pvb.PodNamespace}/{pvb.PersistentVolumeClaim}")
                            .Concat(state.VolumeSnapshots.Where(snapshot => snapshot.BackupName == latestCompleted.Location && snapshot.ReadyToUse)
                                .Select(snapshot => $"{snapshot.Namespace}/{snapshot.ClaimName}"))
                            .ToHashSet(StringComparer.Ordinal);
                        foreach (var pvc in state.PersistentVolumeClaims.Where(pvc =>
                                     string.Equals(pvc.Phase, "Bound", StringComparison.OrdinalIgnoreCase)
                                     && !protectedClaims.Contains($"{pvc.Namespace}/{pvc.Name}")))
                        {
                            yield return new Finding("k8s-pvc/unprotected", Severity.Red, $"{pvc.Namespace}/{pvc.Name}", expected.Name,
                                $"Bound PVC '{pvc.Namespace}/{pvc.Name}' has no completed Velero pod-volume backup in latest Completed backup '{latestCompleted.Location}'.",
                                "Enable and verify a Velero file-system backup or CSI snapshot for this PVC, then restore a test PVC before treating it as protected.");
                        }
                    }
                }
            }

            if (!expected.CheckWorkloads) continue;
            foreach (var pvc in state.PersistentVolumeClaims.Where(p => !string.Equals(p.Phase, "Bound", StringComparison.OrdinalIgnoreCase)))
                yield return new Finding("k8s-pvc/unbound", Severity.Red, $"{pvc.Namespace}/{pvc.Name}", expected.Name,
                    $"PVC '{pvc.Namespace}/{pvc.Name}' is {pvc.Phase}, not Bound.",
                    "Inspect the PVC events and storage class; restore protection cannot rely on an unbound volume.");
            foreach (var service in inventory.Services.Where(s => s.Host == expected.Name && s.Kind == ServiceKind.K8sWorkload && s.State == "degraded"))
                yield return new Finding("k8s-workload/unavailable", Severity.Yellow, service.Name, expected.Name,
                    $"Deployment '{service.Name}' has fewer available replicas than desired.",
                    "Run kubectl describe deployment and kubectl rollout status to investigate.");
            foreach (var service in inventory.Services.Where(s => s.Host == expected.Name && s.Kind == ServiceKind.K8sNode && s.State == "NotReady"))
                yield return new Finding("k8s-node/not-ready", Severity.Red, service.Name, expected.Name,
                    $"Node '{service.Name}' is NotReady.",
                    "Inspect node conditions and kubelet health before trusting cluster backups.");
        }
    }
}
