using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

/// <summary>
/// One deterministic rule: inventory in, findings out. No I/O, no clock, no LLM —
/// everything a rule needs must already be in the model so rules stay golden-file testable.
/// </summary>
public interface ICheck
{
    string RuleId { get; }

    IEnumerable<Finding> Evaluate(LabInventory inventory);
}
