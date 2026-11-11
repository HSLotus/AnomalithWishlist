using System;
using System.Collections.Generic;
using System.Linq;

namespace AnomalithWishlist
{
    // Recipe models and demand arithmetic have no Unity dependency.
    public sealed class RecipeStage
    {
        public string Key = "", Owner = "", Name = "", Category = "";
        public int FromLevel, ToLevel, Money;
        public bool Complete, Repeatable;
        public Dictionary<string, long> Materials = new Dictionary<string, long>(StringComparer.Ordinal);
    }
    public sealed class MaterialDemand
    {
        public long Required;
        public int Stages;
        public HashSet<string> Contents = new HashSet<string>(StringComparer.Ordinal);
        public bool HasFiniteUse, HasRepeatableUse;
    }
    public sealed class MaterialAllocation
    {
        public RecipeStage Stage;
        public int Batches;
        public long Required, Remaining;
    }
    public static class DemandEngine
    {
        public static Dictionary<string, MaterialDemand> Calculate(IEnumerable<RecipeStage> stages,
            IReadOnlyDictionary<string, int> selected, bool all)
        {
            var result = new Dictionary<string, MaterialDemand>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stage in stages)
            {
                if (!seen.Add(stage.Key)) throw new InvalidOperationException("Duplicate recipe identity: " + stage.Key);
                int quantity = all ? (stage.Repeatable ? 0 : 1) : (selected.TryGetValue(stage.Key, out int q) ? Math.Max(0, q) : 0);
                if (!stage.Repeatable) quantity = Math.Min(quantity, 1);
                foreach (var cost in stage.Materials)
                {
                    if (cost.Value < 0) throw new InvalidOperationException("Negative material cost: " + stage.Key);
                    if (cost.Value == 0) continue;
                    if (!result.TryGetValue(cost.Key, out var demand)) result[cost.Key] = demand = new MaterialDemand();
                    if (stage.Repeatable) demand.HasRepeatableUse = true; else demand.HasFiniteUse = true;
                    if (stage.Complete || quantity == 0) continue;
                    demand.Required = checked(demand.Required + checked(cost.Value * quantity));
                    demand.Stages++;
                    demand.Contents.Add(stage.Owner);
                }
            }
            return result;
        }
        // Allocate a shared stock pool ONCE across goals, in stable stage-key order.
        // The sum of Remaining equals the aggregate shortfall, never N independent uses of the same stock.
        public static List<MaterialAllocation> AllocateMaterial(IEnumerable<RecipeStage> stages,
            IReadOnlyDictionary<string, int> selected, string material, long owned)
        {
            var lines = new List<MaterialAllocation>();
            long available = Math.Max(0, owned);
            foreach (var s in stages.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (s.Complete || !selected.TryGetValue(s.Key, out int q) || q <= 0 ||
                    !s.Materials.TryGetValue(material, out long cost) || cost <= 0) continue;
                int batches = s.Repeatable ? q : 1;
                long required = checked(cost * batches);
                long assigned = Math.Min(available, required); available -= assigned;
                lines.Add(new MaterialAllocation { Stage = s, Batches = batches, Required = required, Remaining = required - assigned });
            }
            return lines;
        }
        public static long Shortage(long required, long owned) => Math.Max(0, required - Math.Max(0, owned));
        public static long Surplus(long required, long owned) => Math.Max(0, Math.Max(0, owned) - Math.Max(0, required));
    }
}
