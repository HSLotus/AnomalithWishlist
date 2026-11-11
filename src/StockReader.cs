using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace AnomalithWishlist
{
    public sealed class StockCounts
    {
        public long Bag;
        public long Warehouse;
        public long Staged;
        public long Held;
        public long Owned => Bag + Warehouse + Held;
        public string Note;
        public bool Ready;
    }

    /// <summary>Read-only stock view. Call on Unity's main thread after game UI operations settle.</summary>
    public static class StockReader
    {
        private enum OriginKind { Unknown, Player, Warehouse, Staged }
        private sealed class Origin { public OriginKind Kind; }
        private sealed class RefComparer : IEqualityComparer<InventoryCellItemData>
        {
            public bool Equals(InventoryCellItemData a, InventoryCellItemData b) => ReferenceEquals(a, b);
            public int GetHashCode(InventoryCellItemData value) => RuntimeHelpers.GetHashCode(value);
        }

        private static readonly RefComparer References = new RefComparer();
        private static readonly ConditionalWeakTable<InventoryCellItemData, Origin> Origins =
            new ConditionalWeakTable<InventoryCellItemData, Origin>();
        private static readonly FieldInfo StorageInventory = AccessTools.Field(typeof(StorageMenu), "m_InventoryBag");
        private static readonly FieldInfo StorageWarehouse = AccessTools.Field(typeof(StorageMenu), "m_StorageBag");
        private static readonly FieldInfo StorageCursor = AccessTools.Field(typeof(StorageMenu), "m_Cursor");
        private static readonly FieldInfo SaleOffer = AccessTools.Field(typeof(SaleMenu), "m_InventoryBag");
        private static readonly FieldInfo SaleWarehouse = AccessTools.Field(typeof(SaleMenu), "m_StorageBag");
        private static readonly FieldInfo SaleCursor = AccessTools.Field(typeof(SaleMenu), "m_Cursor");
        private static StorageMenu storage;
        private static SaleMenu sale;
        private static int nextDiscoveryFrame, nextSaleDiscoveryFrame;
        private static int storageTransition, saleTransition;
        private static bool saleUnstable;

        private static InventoryBag GetBag(FieldInfo field, Component menu) =>
            menu != null ? field?.GetValue(menu) as InventoryBag : null;

        private static StorageMenu FindStorage()
        {
            // Keep initialized storage even while a modal covers/disables it. Destroy() clears its bags.
            if (storage != null && GetBag(StorageInventory, storage) != null &&
                GetBag(StorageWarehouse, storage) != null)
                return storage;
            storage = null;
            if (Time.frameCount < nextDiscoveryFrame) return null;
            nextDiscoveryFrame = Time.frameCount + 30;
            // Fallback supports loading the plugin after a menu was already initialized.
            foreach (var candidate in UnityEngine.Object.FindObjectsByType<StorageMenu>(FindObjectsSortMode.None))
            {
                if (GetBag(StorageInventory, candidate) != null && GetBag(StorageWarehouse, candidate) != null)
                {
                    storage = candidate;
                    break;
                }
            }
            return storage;
        }

        private static SaleMenu FindSale()
        {
            // Keep the drained bags while a confirmation dialog covers the sale screen.
            if (sale != null && GetBag(SaleOffer, sale) != null && GetBag(SaleWarehouse, sale) != null)
                return sale;
            sale = null;
            if (Time.frameCount < nextSaleDiscoveryFrame) return null;
            nextSaleDiscoveryFrame = Time.frameCount + 30;
            foreach (var candidate in UnityEngine.Object.FindObjectsByType<SaleMenu>(FindObjectsSortMode.None))
            {
                if (GetBag(SaleOffer, candidate) != null && GetBag(SaleWarehouse, candidate) != null)
                {
                    sale = candidate;
                    break;
                }
            }
            return sale;
        }

        private static InventoryCursorBase GetCursor(StorageMenu menu, SaleMenu saleMenu, InventoryUI ui)
        {
            if (menu != null)
                return (StorageCursor?.GetValue(menu) as InventoryCursorCtrl)?.GetCurrentCursor;
            if (saleMenu != null)
                return (SaleCursor?.GetValue(saleMenu) as InventoryCursorCtrl)?.GetCurrentCursor;
            return ui != null ? ui.Cursor?.GetCurrentCursor : null;
        }

        private static bool IsTransitioning => storageTransition != 0 || saleTransition != 0 || saleUnstable;

        private static bool Contains(InventoryBag bag, InventoryCellItemData item)
        {
            if (bag == null) return false;
            foreach (var candidate in bag.GetItemList())
                if (ReferenceEquals(candidate, item)) return true;
            return false;
        }

        private static bool IsHeldPlayer(InventoryCursorBase cursor, InventoryCellItemData item) =>
            cursor != null && ReferenceEquals(cursor.GetGrabItemData, item) &&
            Origins.TryGetValue(item, out var origin) && origin.Kind == OriginKind.Player;

        /// <summary>Bag identity only. For tile highlighting use IsPlayerItem to exclude predictions.</summary>
        public static bool IsPlayerBag(InventoryBag bag)
        {
            if (bag == null || IsTransitioning) return false;
            try
            {
                var menu = FindStorage();
                var saleMenu = FindSale();
                if (menu != null && saleMenu != null) return false;
                if (menu != null) return ReferenceEquals(bag, GetBag(StorageInventory, menu));
                if (saleMenu != null && ReferenceEquals(bag, GetBag(SaleOffer, saleMenu))) return true;
                var inventory = SingletonMonoBehaviour<Inventory>.Instance;
                return inventory != null && ReferenceEquals(bag, inventory.BagData);
            }
            catch { return false; }
        }

        /// <summary>Actual backpack or sale-offer members/held player-side items; excludes predictions.</summary>
        public static bool IsPlayerItem(InventoryCellItemData item)
        {
            if (item == null || IsTransitioning) return false;
            try
            {
                var inventory = SingletonMonoBehaviour<Inventory>.Instance;
                if (inventory == null) return false;
                var menu = FindStorage();
                var saleMenu = FindSale();
                if (menu != null && saleMenu != null) return false;
                var bag = menu != null ? GetBag(StorageInventory, menu) : inventory.BagData;
                if (Contains(bag, item) || (saleMenu != null && Contains(GetBag(SaleOffer, saleMenu), item))) return true;
                var ui = inventory.GetInventoryUI;
                if (IsHeldPlayer(GetCursor(menu, saleMenu, ui), item)) return true;
                // SaleMenu does not replace the backpack. Account for a previously held native
                // backpack item too, without classifying the sale warehouse cursor as player-side.
                return saleMenu != null && IsHeldPlayer(ui != null ? ui.Cursor?.GetCurrentCursor : null, item);
            }
            catch { return false; }
        }

        public static StockCounts Read(string materialId)
        {
            var counts = new StockCounts();
            if (string.IsNullOrEmpty(materialId)) { counts.Note = "No material ID."; return counts; }
            if (IsTransitioning)
            {
                counts.Note = saleUnstable ? "Sale transfer failed; stock is uncertain until the menu recovers."
                    : "Storage/sale is transferring items; retry next frame.";
                return counts;
            }
            try
            {
                var inventory = SingletonMonoBehaviour<Inventory>.Instance;
                if (inventory == null || inventory.BagData == null)
                { counts.Note = "Inventory is not ready."; return counts; }
                string id = Inventory.ConvertItemID(materialId);
                var menu = FindStorage();
                var saleMenu = FindSale();
                if (menu != null && saleMenu != null)
                { counts.Note = "Multiple temporary repository views are active; stock is uncertain."; return counts; }
                var ui = inventory.GetInventoryUI;
                var playerBag = menu != null ? GetBag(StorageInventory, menu) : inventory.BagData;
                var warehouseBag = menu != null ? GetBag(StorageWarehouse, menu) : null;
                var offerBag = saleMenu != null ? GetBag(SaleOffer, saleMenu) : null;
                var saleWarehouse = saleMenu != null ? GetBag(SaleWarehouse, saleMenu) : null;
                var stagedBag = menu == null && ui != null ? ui.TempBag : null;
                if (playerBag == null || (menu != null && warehouseBag == null) ||
                    (saleMenu != null && (offerBag == null || saleWarehouse == null)))
                { counts.Note = "Storage/sale bags are not ready."; return counts; }

                var seen = new HashSet<InventoryCellItemData>(References);
                counts.Bag = Sum(playerBag, id, seen);
                counts.Warehouse = Sum(warehouseBag, id, seen);
                // SaleMenu's left bag is an initially EMPTY offer basket, not a backpack copy.
                // Its items still belong to the player until InventoryItemSale consumes them.
                // Both sale bags originate in the repository; keep them in Warehouse, alongside
                // the real backpack (counted only once above) and undrained repository categories.
                counts.Warehouse += Sum(offerBag, id, seen) + Sum(saleWarehouse, id, seen);
                counts.Staged = Sum(stagedBag, id, seen);
                // Do not include KeyItemList: native recipes cannot consume it.
                counts.Warehouse += Math.Max(0, inventory.GetRepositoryItemCount(id));
                bool unknownHeld = AddHeld(counts, GetCursor(menu, saleMenu, ui)?.GetGrabItemData, id, seen);
                if (saleMenu != null && ui != null)
                    unknownHeld |= AddHeld(counts, ui.Cursor?.GetCurrentCursor?.GetGrabItemData, id, seen);
                counts.Ready = !unknownHeld;
                counts.Note = unknownHeld
                    ? "Held item origin is unknown; excluded from Owned until released."
                    : counts.Staged > 0 ? "Staged items are not included in Owned." : null;
            }
            catch (Exception ex)
            {
                counts = new StockCounts { Note = "Stock unavailable: " + ex.GetType().Name, Ready = false };
            }
            return counts;
        }

        // Returns true only for an untracked held stack of this material. Reference identity
        // deduplicates held objects still present in a bag and repeated/shared cursor references.
        private static bool AddHeld(StockCounts counts, InventoryCellItemData held, string id,
            HashSet<InventoryCellItemData> seen)
        {
            if (held == null || !Matches(held, id) || !seen.Add(held)) return false;
            long amount = Math.Max(0, held.currentStackCount);
            var origin = Origins.TryGetValue(held, out var tracked) ? tracked.Kind : OriginKind.Unknown;
            if (origin == OriginKind.Player || origin == OriginKind.Warehouse) counts.Held += amount;
            else if (origin == OriginKind.Staged) counts.Staged += amount;
            else return amount > 0;
            return false;
        }

        private static bool Matches(InventoryCellItemData item, string id) =>
            item != null && string.Equals(Inventory.ConvertItemID(item.GetItemID), id, StringComparison.Ordinal);

        private static long Sum(InventoryBag bag, string id, HashSet<InventoryCellItemData> seen)
        {
            long result = 0;
            if (bag == null) return result;
            foreach (var item in bag.GetItemList())
                if (item != null && seen.Add(item) && Matches(item, id))
                    result += Math.Max(0, item.currentStackCount);
            return result;
        }

        private static OriginKind Classify(InventoryBag bag)
        {
            if (bag == null) return OriginKind.Unknown;
            if (IsTransitioning) return OriginKind.Unknown;
            var menu = FindStorage();
            var saleMenu = FindSale();
            if (menu != null && saleMenu != null) return OriginKind.Unknown;
            if (menu != null)
            {
                if (ReferenceEquals(bag, GetBag(StorageInventory, menu))) return OriginKind.Player;
                if (ReferenceEquals(bag, GetBag(StorageWarehouse, menu))) return OriginKind.Warehouse;
                return OriginKind.Unknown;
            }
            if (saleMenu != null)
            {
                if (ReferenceEquals(bag, GetBag(SaleOffer, saleMenu))) return OriginKind.Player;
                if (ReferenceEquals(bag, GetBag(SaleWarehouse, saleMenu))) return OriginKind.Warehouse;
            }
            var inventory = SingletonMonoBehaviour<Inventory>.Instance;
            if (inventory == null) return OriginKind.Unknown;
            if (ReferenceEquals(bag, inventory.BagData)) return OriginKind.Player;
            var ui = inventory.GetInventoryUI;
            if (ui != null && ReferenceEquals(bag, ui.TempBag)) return OriginKind.Staged;
            return OriginKind.Unknown;
        }

        private static void Remember(InventoryBag bag, InventoryCellItemData item)
        {
            if (item == null) return;
            try
            {
                var origin = Origins.GetValue(item, _ => new Origin());
                origin.Kind = Classify(bag);
            }
            catch { /* A UI observer must never break native dragging. */ }
        }

        // Captures source before currentBag changes to the hovered destination.
        [HarmonyPatch(typeof(InventoryBag), nameof(InventoryBag.GrabItem),
            new Type[] { typeof(InventoryCellItemData), typeof(bool) })]
        private static class GrabKnownItemPatch
        {
            private static void Prefix(InventoryBag __instance, InventoryCellItemData itemData) =>
                Remember(__instance, itemData);
        }

        // PutItem's item-swap path uses this overload, bypassing SetGrabItem.
        [HarmonyPatch(typeof(InventoryBag), nameof(InventoryBag.GrabItem),
            new Type[] { typeof(InventoryCellItemData.AreaInfo) })]
        private static class GrabSwapItemPatch
        {
            private static void Postfix(InventoryBag __instance, InventoryCellItemData __result) =>
                Remember(__instance, __result);
        }

        // SaleMenu drains the repository into its right bag, consumes only its left offer bag
        // on sale, and returns BOTH unsold bags on Destroy. Never observe mid-transfer totals.
        [HarmonyPatch(typeof(SaleMenu), "Initialize")]
        private static class SaleInitializePatch
        {
            private static void Prefix(SaleMenu __instance)
            {
                saleTransition++;
                sale = __instance;
            }
            private static void Finalizer(Exception __exception)
            {
                saleTransition = Math.Max(0, saleTransition - 1);
                nextSaleDiscoveryFrame = 0;
                saleUnstable = __exception != null;
            }
        }

        [HarmonyPatch(typeof(SaleMenu), "InventoryItemSale")]
        private static class SaleConsumePatch
        {
            private static void Prefix(SaleMenu __instance)
            {
                saleTransition++;
                sale = __instance;
            }
            private static void Finalizer(Exception __exception)
            {
                saleTransition = Math.Max(0, saleTransition - 1);
                if (__exception != null) saleUnstable = true;
            }
        }

        [HarmonyPatch(typeof(SaleMenu), nameof(SaleMenu.Destroy))]
        private static class SaleDestroyPatch
        {
            private static void Prefix(SaleMenu __instance)
            {
                saleTransition++;
                sale = __instance;
            }
            private static void Finalizer(SaleMenu __instance, Exception __exception)
            {
                saleTransition = Math.Max(0, saleTransition - 1);
                nextSaleDiscoveryFrame = 0;
                saleUnstable = __exception != null;
                if (__exception == null && ReferenceEquals(sale, __instance)) sale = null;
            }
        }

        [HarmonyPatch(typeof(StorageMenu), nameof(StorageMenu.Initialize))]
        private static class StorageInitializePatch
        {
            private static void Prefix(StorageMenu __instance)
            {
                storageTransition++;
                storage = __instance;
            }
            private static void Finalizer(Exception __exception)
            {
                storageTransition = Math.Max(0, storageTransition - 1);
                nextDiscoveryFrame = 0;
                if (__exception != null) storage = null;
            }
        }

        [HarmonyPatch(typeof(StorageMenu), nameof(StorageMenu.Destroy))]
        private static class StorageDestroyPatch
        {
            private static void Prefix() => storageTransition++;
            private static void Finalizer(StorageMenu __instance)
            {
                storageTransition = Math.Max(0, storageTransition - 1);
                if (ReferenceEquals(storage, __instance)) storage = null;
                nextDiscoveryFrame = 0;
            }
        }
    }
}
