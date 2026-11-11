using HarmonyLib;

namespace AnomalithWishlist
{
    // The legacy modal/input-blocking patches and yellow rectangle implementation were removed.
    // Native descriptions, corner markers and settings are implemented in their respective files.
    [HarmonyPatch(typeof(SkillDatas), nameof(SkillDatas.AcquisitionSkillTree))]
    internal static class LearnedSkillPatch
    {
        private static void Postfix() { Plugin.Self?.RequestRefresh(); }
    }

    [HarmonyPatch(typeof(ConsumablesDevMenu), "DevelopMent")]
    internal static class CraftedPatch
    {
        private static void Postfix(DevelopmetData data) { Plugin.Self?.Crafted(data); }
    }
}
