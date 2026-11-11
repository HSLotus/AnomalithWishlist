using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AnomalithWishlist
{
    // The workshop SALE action opens ItemSaleDialog, NOT the legacy bag-based SaleMenu.
    // Open attaches on ordinary entry; Calculation also attaches after readiness/reenable
    // while the same dialog is already open. Observe only; never change sale selection/data.
    [HarmonyPatch]
    internal static class NativeWorkshopSaleObserverPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ItemSaleDialog), nameof(ItemSaleDialog.Open));
            yield return AccessTools.Method(typeof(ItemSaleDialog), "Calculation");
        }
        private static void Postfix(ItemSaleDialog __instance) => NativeStoragePopups.Observe(__instance);
    }

    internal static class NativeWorkshopSalePopup
    {
        internal sealed class Selection
        {
            internal RepositoryElement Row;
            internal SaveItemList.ItemInfo Item;
            internal ItemSaleDialog.ItemSaleData Staged;
            internal string Id => Staged != null ? Staged.GetItemGUID() : Item.GetItemID;
            internal string BaseId => Staged != null ? Staged.GetItemId() : Item.itemID;
            internal int Count => Staged != null ? Staged.GetItemCount() : Item.num;
            internal RectTransform Anchor => Row != null ? Row.GetRectTransform : null;

            internal bool SameEntry(Selection other) => other != null && Row == other.Row &&
                ReferenceEquals(Item, other.Item) && ReferenceEquals(Staged, other.Staged);
        }

        private static List<ButtonElement> Rows(ItemSaleDialog owner) =>
            NativeDescriptions.Field<List<ButtonElement>>(owner, "m_CurrentTabElements");
        private static List<ItemSaleDialog.ItemSaleData> Sales(ItemSaleDialog owner) =>
            NativeDescriptions.Field<List<ItemSaleDialog.ItemSaleData>>(owner, "m_SaleItemList");
        private static DialogItemSend Send(ItemSaleDialog owner) =>
            NativeDescriptions.Field<DialogItemSend>(owner, "m_ItemSend");

        internal static Selection Select(ItemSaleDialog owner, bool mouse)
        {
            var rows = Rows(owner);
            if (mouse)
            {
                // Staged entries are presentation-only rows: AddBeforeItem does NOT set
                // ItemInfo or ButtonElement.ID. UpdateSaleItemList creates them in exactly
                // m_SaleItemList order. Preserve the GUID, including artifact instances.
                var send = Send(owner);
                var stagedRows = send != null ? send.GetBeforElements() : null;
                var sales = Sales(owner);
                if (stagedRows != null && sales != null && stagedRows.Count == sales.Count)
                    for (int i = 0; i < stagedRows.Count; i++)
                        if (stagedRows[i] is RepositoryElement stagedRow && Hovered(stagedRow) &&
                            sales[i] != null && sales[i].GetItemCount() > 0)
                            return new Selection { Row = stagedRow, Staged = sales[i] };
                if (rows != null)
                    foreach (var element in rows)
                        if (element is RepositoryElement row && Hovered(row) && row.GetItemInfo() != null)
                            return new Selection { Row = row, Item = row.GetItemInfo() };
                return null;
            }
            // Native pad navigation selects the left-hand rows (including checked/staged
            // ones). The right-hand summary has scrolling, but NO native selection cursor;
            // do not invent a focus mode or consume submit/cancel/left-stick navigation.
            var selected = NativeDescriptions.Field<ButtonElement>(owner, "m_CurrentItemElement") as RepositoryElement;
            if (selected == null || rows == null || !rows.Contains(selected) || !Available(selected) || selected.GetItemInfo() == null)
                return null;
            return new Selection { Row = selected, Item = selected.GetItemInfo() };
        }

        internal static bool IsLive(ItemSaleDialog owner, Selection selection)
        {
            if (selection == null || !Available(selection.Row)) return false;
            if (selection.Staged == null)
            {
                var rows = Rows(owner);
                return rows != null && rows.Contains(selection.Row) &&
                    ReferenceEquals(selection.Row.GetItemInfo(), selection.Item);
            }
            var send = Send(owner);
            var stagedRows = send != null ? send.GetBeforElements() : null;
            var sales = Sales(owner);
            if (stagedRows == null || sales == null || stagedRows.Count != sales.Count) return false;
            int index = stagedRows.IndexOf(selection.Row);
            return index >= 0 && ReferenceEquals(sales[index], selection.Staged) && selection.Staged.GetItemCount() > 0;
        }

        private static bool Hovered(RepositoryElement row) => Available(row) && row.GetMouseHandler.IsOverlap;

        private static bool Available(RepositoryElement row)
        {
            if (row == null || !row.isActiveAndEnabled || !row.gameObject.activeInHierarchy ||
                !row.isEnable || row.GetRectTransform == null || row.GetMouseHandler.IsDrag) return false;
            // Also invalidate a retained popup when its row scrolls completely out of view.
            // MouseHandler accounts for the native mask when hovering; retained/pad entries
            // need the same visibility protection even when the pointer is over the popup.
            var scroll = row.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.viewport == null) return true;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, row.GetRectTransform);
            Rect viewport = scroll.viewport.rect;
            return bounds.max.x > viewport.xMin && bounds.min.x < viewport.xMax &&
                bounds.max.y > viewport.yMin && bounds.min.y < viewport.yMax;
        }
    }
}
