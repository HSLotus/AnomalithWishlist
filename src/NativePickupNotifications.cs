using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnomalithWishlist
{
    // Observe actual acquisition calls and ID-bearing native toasts. Never infer an ID
    // from a translated name, mutate inventory, or treat warehouse transfers as pickups.
    internal static class NativePickupNotifications
    {
        internal static readonly FieldInfo LogText = AccessTools.Field(typeof(ItemLog), "textUI");
        private static readonly HashSet<WishlistPickupBadge> Badges = new HashSet<WishlistPickupBadge>();
        private static readonly Dictionary<string, Recent> Finished = new Dictionary<string, Recent>(StringComparer.Ordinal);
        private static readonly List<Pending> Queue = new List<Pending>();
        private static readonly HashSet<string> Announced = new HashSet<string>(StringComparer.Ordinal);
        [ThreadStatic] private static Transaction transaction;
        [ThreadStatic] private static string logItem;
        private static object lastSave;
        private static bool reported;
        private static int lastCheckedRevision = -1;

        private sealed class Snapshot
        {
            internal object Save;
            internal string Plan;
            internal Dictionary<string, long> Owned;
            internal Dictionary<string, RecipeStage> Stages;
            internal HashSet<string> Ready;
        }
        private sealed class Transaction
        {
            internal Transaction Parent;
            internal string Id;
            internal Snapshot Before;
        }
        private sealed class Recent { internal int Frame; internal Snapshot Before; }
        private sealed class Pending { internal string Id; internal Snapshot Before; internal float Deadline; }

        private static void Report(Exception e)
        {
            if (reported) return;
            reported = true;
            Debug.LogWarning("[AnomalithWishlist] Acquisition observer skipped safely: " + e);
        }
        internal static void Track(WishlistPickupBadge badge) => Badges.Add(badge);
        internal static void Forget(WishlistPickupBadge badge) => Badges.Remove(badge);
        internal static void Reset()
        {
            Queue.Clear(); Finished.Clear(); Announced.Clear(); lastSave = null; lastCheckedRevision = -1;
        }
        internal static void Shutdown()
        {
            Reset(); transaction = null; logItem = null;
            foreach (var badge in Badges.ToArray()) if (badge != null) badge.Release();
            Badges.Clear();
        }

        private static Snapshot Capture()
        {
            var plugin = Plugin.Self;
            if (plugin == null || !plugin.FeatureEnabled || !plugin.Ready || SaveRootGame.Inventory == null) return null;
            var stages = plugin.Stages.Where(s => !s.Complete && plugin.IsSelected(s.Key))
                .OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
            if (stages.Count == 0) return null;
            var owned = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string id in stages.SelectMany(s => s.Materials.Where(p => p.Value > 0).Select(p => p.Key)).Distinct())
            {
                var stock = StockReader.Read(id);
                if (!stock.Ready) return null; // Never interpret uncertain stock as zero.
                owned[id] = stock.Owned;
            }
            var ready = new HashSet<string>(stages.Select(s => s.Key), StringComparer.Ordinal);
            foreach (var pair in owned)
                foreach (var line in DemandEngine.AllocateMaterial(stages, plugin.SelectedTargets, pair.Key, pair.Value))
                    if (line.Remaining > 0) ready.Remove(line.Stage.Key);
            return new Snapshot {
                Save = SaveRootGame.Inventory,
                Plan = string.Join("\n", stages.Select(s => s.Key + ":" + plugin.SelectedQuantity(s.Key))),
                Owned = owned, Stages = stages.ToDictionary(s => s.Key, StringComparer.Ordinal), Ready = ready
            };
        }
        private static Transaction Begin(string id, int quantity)
        {
            if (quantity <= 0 || string.IsNullOrEmpty(id)) return null;
            try
            {
                var plugin = Plugin.Self;
                if (plugin == null || !plugin.FeatureEnabled || !plugin.Ready) return null;
                id = Inventory.ConvertItemID(id);
                if (!plugin.ManualDemands.TryGetValue(id, out var demand) || demand.Required <= 0) return null;
                var value = new Transaction { Parent = transaction, Id = id, Before = Capture() };
                transaction = value;
                return value;
            }
            catch (Exception e) { Report(e); return null; }
        }
        private static void Finish(Transaction value, Exception error)
        {
            if (value == null) return;
            try
            {
                if (error == null && value.Before != null)
                    Finished[value.Id] = new Recent { Frame = Time.frameCount, Before = value.Before };
            }
            catch (Exception e) { Report(e); }
            finally { transaction = value.Parent; }
        }
        private static string EnterLog(string id, int quantity)
        {
            string previous = logItem;
            logItem = string.IsNullOrEmpty(id) ? null : Inventory.ConvertItemID(id);
            try
            {
                if (quantity <= 0 || logItem == null || logItem == previous) return previous;
                Snapshot before = null;
                for (var current = transaction; current != null; current = current.Parent)
                    if (current.Id == logItem && current.Before != null) before = current.Before;
                if (before == null && Finished.TryGetValue(logItem, out var recent) && recent.Frame == Time.frameCount)
                    before = recent.Before;
                if (before != null)
                    Queue.Add(new Pending { Id = logItem, Before = before, Deadline = Time.unscaledTime + 2 });
                Plugin.Self?.RequestRefresh();
            }
            catch (Exception e) { Report(e); }
            return previous;
        }

        // Called after the main plugin refresh, never from inside a native item mutation
        // or a nested ItemLog.Initialize. This prevents notification recursion/reentrancy.
        internal static void Tick()
        {
            try
            {
                foreach (var id in Finished.Where(p => p.Value.Frame < Time.frameCount).Select(p => p.Key).ToArray()) Finished.Remove(id);
                Queue.RemoveAll(p => p.Deadline < Time.unscaledTime);
                var plugin = Plugin.Self;
                if (plugin == null || !plugin.FeatureEnabled || !plugin.Ready) { Reset(); return; }
                if (!ReferenceEquals(lastSave, SaveRootGame.Inventory))
                {
                    Announced.Clear(); lastSave = SaveRootGame.Inventory;
                }
                // Re-arm a goal only after a known shortfall; an uncertain read must not
                // manufacture a second completion toast for an already stocked goal.
                if (Queue.Count == 0 && (Announced.Count == 0 || lastCheckedRevision == plugin.Revision)) return;
                lastCheckedRevision = plugin.Revision;
                var after = Capture();
                if (after == null) return;
                Announced.RemoveWhere(key => !after.Stages.ContainsKey(key) || !after.Ready.Contains(key));
                foreach (var pending in Queue)
                {
                    var before = pending.Before;
                    if (!ReferenceEquals(before.Save, after.Save) || before.Plan != after.Plan ||
                        !before.Owned.TryGetValue(pending.Id, out long oldCount) ||
                        !after.Owned.TryGetValue(pending.Id, out long newCount) || newCount <= oldCount) continue;
                    foreach (string key in after.Ready)
                    {
                        if (before.Ready.Contains(key) || Announced.Contains(key)) continue;
                        var stage = after.Stages[key];
                        if (!stage.Materials.TryGetValue(pending.Id, out long cost) || cost <= 0) continue;
                        Announced.Add(key);
                        plugin.Notify(Plugin.StageName(stage) + "需求已经达到要求");
                    }
                }
                Queue.Clear();
            }
            catch (Exception e) { Queue.Clear(); Report(e); }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new Type[] {
            typeof(string), typeof(ArtifactInfo), typeof(int), typeof(Inventory.RemainderItem), typeof(bool), typeof(bool) })]
        private static class AcquisitionPatch
        {
            private static void Prefix(string itemLabel, int num, out Transaction __state)
                => __state = Begin(itemLabel, num);
            private static void Finalizer(Transaction __state, Exception __exception) => Finish(__state, __exception);
        }
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItemRepository))]
        private static class RepositoryAcquisitionPatch
        {
            private static void Prefix(string itemID, int num, out Transaction __state)
                => __state = Begin(itemID, num);
            private static void Finalizer(Transaction __state, Exception __exception) => Finish(__state, __exception);
        }
        [HarmonyPatch(typeof(ItemLogger), nameof(ItemLogger.AddItemForID))]
        private static class IdLogPatch
        {
            private static void Prefix(string itemID, int num, out string __state) => __state = EnterLog(itemID, num);
            private static void Finalizer(string __state) => logItem = __state;
        }
        [HarmonyPatch(typeof(ItemLogger), nameof(ItemLogger.AddIKeytemForID))]
        private static class KeyIdLogPatch
        {
            private static void Prefix(string itemID, int num, out string __state) => __state = EnterLog(itemID, num);
            private static void Finalizer(string __state) => logItem = __state;
        }
        [HarmonyPatch(typeof(ItemLogger), nameof(ItemLogger.AddItem), new Type[] { typeof(InventoryItemInfo.ItemInfo), typeof(int) })]
        private static class InfoLogPatch
        {
            private static void Prefix(InventoryItemInfo.ItemInfo itemInfo, int num, out string __state)
                => __state = EnterLog(itemInfo?.itemLabel, num);
            private static void Finalizer(string __state) => logItem = __state;
        }
        [HarmonyPatch(typeof(ItemLog), nameof(ItemLog.Initialize))]
        private static class RowPatch
        {
            private static void Postfix(ItemLog __instance)
            {
                if (string.IsNullOrEmpty(logItem) || Plugin.Self == null || !Plugin.Self.FeatureEnabled) return;
                try
                {
                    var badge = __instance.GetComponent<WishlistPickupBadge>();
                    if (badge == null) badge = __instance.gameObject.AddComponent<WishlistPickupBadge>();
                    badge.Bind(__instance, logItem);
                }
                catch (Exception e) { Report(e); }
            }
        }
    }

    // The native message/quantity/font/lifetime are left untouched. A non-interactive
    // icon gets a reversible text gutter INSIDE the toast, respecting parent clipping.
    internal sealed class WishlistPickupBadge : MonoBehaviour
    {
        private string itemId;
        private TextMeshProUGUI text;
        private RectTransform badge;
        private WishlistVectorGlyph glyph;
        private float nextRead;
        private int marker;
        private Vector4 originalMargin;
        private bool marginApplied;
        internal void Bind(ItemLog row, string id)
        {
            Hide();
            itemId = id;
            text = NativePickupNotifications.LogText.GetValue(row) as TextMeshProUGUI;
            if (text != null) originalMargin = text.margin;
            nextRead = 0;
            NativePickupNotifications.Track(this);
        }
        private void Hide()
        {
            if (badge != null) badge.gameObject.SetActive(false);
            if (marginApplied && text != null) text.margin = originalMargin;
            marginApplied = false;
        }
        internal void Release()
        {
            enabled = false;
            if (badge != null) { badge.gameObject.SetActive(false); Destroy(badge.gameObject); }
            Destroy(this);
        }
        private void OnDestroy() { Hide(); NativePickupNotifications.Forget(this); }
        private void OnDisable() => Hide();
        private float FitSize()
        {
            float width = text.rectTransform.rect.width - originalMargin.x - originalMargin.z;
            float height = text.rectTransform.rect.height - originalMargin.y - originalMargin.w;
            if (width <= 0 || height < 16) return 0;
            float originalHeight = text.GetPreferredValues(text.text, width, float.PositiveInfinity).y;
            // Preserve at least the original native line capacity. Do not truncate a
            // long name/quantity just to force a badge into an undersized toast prefab.
            foreach (float candidate in new[] { 44f, 32f, 22f, 14f })
            {
                float size = Mathf.Min(candidate, height - 2);
                float available = width - size - 14;
                if (size >= 14 && available > 0 &&
                    text.GetPreferredValues(text.text, available, float.PositiveInfinity).y <= Mathf.Max(height, originalHeight) + 0.5f)
                    return size;
            }
            return 0;
        }
        private void LateUpdate()
        {
            try
            {
                var plugin = Plugin.Self;
                if (text == null || plugin == null || !plugin.FeatureEnabled || !plugin.Ready)
                { Hide(); return; }
                if (Time.unscaledTime >= nextRead)
                {
                    nextRead = Time.unscaledTime + 0.1f;
                    marker = 0;
                    if (plugin.ManualDemands.TryGetValue(itemId, out var demand) && demand.Required > 0)
                    {
                        var stock = StockReader.Read(itemId);
                        if (stock.Ready) marker = stock.Owned >= demand.Required ? 2 : 1;
                    }
                }
                if (marker == 0) { Hide(); return; }
                if (badge == null)
                {
                    var go = new GameObject("Wishlist Pickup Mark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
                    badge = go.GetComponent<RectTransform>();
                    badge.SetParent(text.transform, false);
                    badge.anchorMin = badge.anchorMax = new Vector2(0, 0.5f);
                    badge.pivot = new Vector2(0, 0.5f);
                    badge.anchoredPosition = new Vector2(6, 0);
                    go.GetComponent<LayoutElement>().ignoreLayout = true;
                    var image = go.GetComponent<Image>(); image.raycastTarget = false; image.color = new Color(0.035f, 0.045f, 0.055f, 0.92f);
                    var icon = new GameObject("Wishlist Glyph", typeof(RectTransform), typeof(CanvasRenderer), typeof(WishlistVectorGlyph));
                    var rect = icon.GetComponent<RectTransform>(); rect.SetParent(badge, false);
                    rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                    rect.offsetMin = new Vector2(4, 4); rect.offsetMax = new Vector2(-4, -4);
                    glyph = icon.GetComponent<WishlistVectorGlyph>(); glyph.raycastTarget = false;
                }
                // Toast rows can be shorter than item tiles. Respect their actual row
                // height to avoid overlapping adjacent acquisition messages.
                float size = FitSize();
                if (size < 14) { Hide(); return; }
                badge.sizeDelta = new Vector2(size, size);
                text.margin = new Vector4(originalMargin.x + size + 14, originalMargin.y, originalMargin.z, originalMargin.w);
                marginApplied = true;
                glyph.Shape = marker == 2 ? WishlistVectorGlyph.Glyph.Check : WishlistVectorGlyph.Glyph.Diamond;
                glyph.color = marker == 2 ? NativeWishlistUI.StockedColor : NativeWishlistUI.ShortColor;
                badge.gameObject.SetActive(true);
            }
            catch (Exception e)
            {
                if (badge != null) badge.gameObject.SetActive(false);
                enabled = false;
                Debug.LogWarning("[AnomalithWishlist] Pickup marker stopped safely: " + e.Message);
            }
        }
    }
}
