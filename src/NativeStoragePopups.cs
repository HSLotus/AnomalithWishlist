using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnomalithWishlist
{
    // These screens do not reliably drive ItemPopUp.SetPopUp. In particular RepositoryUI
    // never calls the repository overload, and its optional detail window is not constructed.
    // Observe selection AFTER native navigation/scrolling instead of treating a render hook as
    // a selection event. Nothing here invokes cursor operations or writes to either bag.
    [HarmonyPatch]
    internal static class NativeStorageObserverPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(RepositoryUI), typeof(StorageMenu), typeof(SaleMenu) })
            {
                yield return AccessTools.Method(type, "Initialize");
                yield return AccessTools.Method(type, "Calculation");
            }
        }
        private static void Postfix(DialogBase __instance) => NativeStoragePopups.Observe(__instance);
    }

    [HarmonyPatch]
    internal static class NativeStorageActivationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ItemPopUp), nameof(ItemPopUp.ActiveInventoryPopUp));
            yield return AccessTools.Method(typeof(ItemPopUp), nameof(ItemPopUp.ActiveRepositoryPopUp));
        }
        private static bool Prefix(ItemPopUp __instance) => NativeStoragePopups.AllowNativeWrite(__instance);
    }

    [HarmonyPatch(typeof(DialogBase), nameof(DialogBase.Close))]
    internal static class NativeStorageClosePatch
    {
        private static void Prefix(DialogBase __instance)
        {
            var observer = __instance.GetComponent<NativeStoragePopupObserver>();
            if (observer != null) observer.Release();
        }
    }

    internal static class NativeStoragePopups
    {
        private static readonly Dictionary<ItemPopUp, NativeStoragePopupObserver> Owners =
            new Dictionary<ItemPopUp, NativeStoragePopupObserver>();

        internal static void Observe(DialogBase owner)
        {
            if (Plugin.Self == null || !Plugin.Self.FeatureEnabled || !Plugin.Self.Ready) return;
            if (owner.GetComponent<NativeStoragePopupObserver>() == null)
                owner.gameObject.AddComponent<NativeStoragePopupObserver>();
        }
        internal static bool IsManaged(ItemPopUp popup) => popup != null && Owners.ContainsKey(popup);
        internal static bool AllowNativeWrite(ItemPopUp popup) => !Owners.TryGetValue(popup, out var owner) || owner.Rendering;
        internal static bool OwnsInput(ItemPopUp popup, DialogBase top) =>
            Owners.TryGetValue(popup, out var owner) && owner != null && owner.OwnsInput(top);
        internal static void Register(ItemPopUp popup, NativeStoragePopupObserver owner)
        {
            if (popup != null) Owners[popup] = owner;
        }
        internal static void Unregister(ItemPopUp popup)
        {
            if (!ReferenceEquals(popup, null)) Owners.Remove(popup);
        }
        internal static void Shutdown()
        {
            foreach (var observer in Resources.FindObjectsOfTypeAll<NativeStoragePopupObserver>())
            {
                if (observer == null) continue;
                observer.Release();
                UnityEngine.Object.Destroy(observer);
            }
            Owners.Clear();
        }
    }

    // Late order matters: SaleMenu/StorageMenu have their own LateUpdate which otherwise
    // resurrects the grabbed item's popup. Our lease suppresses those writes, then validates
    // selection/modal ownership once more before rendering.
    [DefaultExecutionOrder(15000)]
    public sealed class NativeStoragePopupObserver : MonoBehaviour
    {
        private DialogBase owner;
        private ItemPopUp originalPopup, renderer;
        private RectTransform host, root;
        private GameObject repositoryRoot;
        private Canvas canvas;
        private bool cloned;
        internal bool Rendering { get; private set; }
        private InventoryCellItemData lastCell;
        private NativeWorkshopSalePopup.Selection lastSale;
        private SaveItemList.ItemInfo lastRepository;
        private InventoryBag lastBag;
        private Vector2Int lastPosition;
        private RectTransform lastAnchor;
        private int lastCount, lastScreenWidth, lastScreenHeight;
        private string lastId;
        private Vector3 lastAnchorPosition;
        private Vector2 lastAnchorSize;
        private float retryAt;
        private RootState savedRoot;
        private string oldName, oldDetail, oldPrice;
        private bool oldPriceActive;
        private readonly Dictionary<GameObject, bool> nativeActive = new Dictionary<GameObject, bool>();
        private readonly Dictionary<TextMeshProUGUI, bool> nativeTextEnabled = new Dictionary<TextMeshProUGUI, bool>();

        private struct RootState
        {
            internal Transform Parent;
            internal int Sibling;
            internal Vector2 Min, Max, Pivot, Size;
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
            internal RootState(RectTransform r)
            {
                Parent = r.parent; Sibling = r.GetSiblingIndex();
                Min = r.anchorMin; Max = r.anchorMax; Pivot = r.pivot; Size = r.sizeDelta;
                Position = r.anchoredPosition3D; Scale = r.localScale; Rotation = r.localRotation;
            }
            internal void Restore(RectTransform r)
            {
                r.SetParent(Parent, false); r.SetSiblingIndex(Sibling);
                r.anchorMin = Min; r.anchorMax = Max; r.pivot = Pivot; r.sizeDelta = Size;
                r.anchoredPosition3D = Position; r.localScale = Scale; r.localRotation = Rotation;
            }
        }

        private void Awake() { owner = GetComponent<DialogBase>(); }
        private bool Enabled => Plugin.Self != null && Plugin.Self.FeatureEnabled && Plugin.Self.Ready;
        private InventoryCursorCtrl Cursor => NativeDescriptions.Field<InventoryCursorCtrl>(owner,
            owner is RepositoryUI ? "cursorCtrl" : "m_Cursor");
        // DeviceManager inherits MonoSingleton from the game's external first-pass
        // assembly. Cache reflection rather than adding that assembly to the mod's refs.
        private static readonly PropertyInfo DeviceInstance = AccessTools.Property(typeof(DeviceManager), "Instance");
        private static readonly PropertyInfo DeviceInput = AccessTools.Property(typeof(DeviceManager), "CurrentInputType");
        private static readonly PropertyInfo DeviceMouse = AccessTools.Property(typeof(DeviceManager), "GetMousePosition");
        private static object Device => DeviceInstance?.GetValue(null);
        private static bool MouseMode
        {
            get
            {
                var device = Device;
                return device != null && DeviceInput != null &&
                    (DeviceManager.InputType)DeviceInput.GetValue(device) == DeviceManager.InputType.KeyBoard;
            }
        }

        internal bool OwnsInput(DialogBase top)
        {
            if (!Enabled || owner == null || top != owner || !owner.isActiveAndEnabled || owner.IsDisable ||
                owner.IsDisableOperation || root == null || !root.gameObject.activeInHierarchy) return false;
            // ItemSaleDialog owns repository rows, not an InventoryCursorCtrl. Its popup
            // still participates in the same exact-owner scroll lease as warehouse popups.
            if (owner is ItemSaleDialog sale) return NativeWorkshopSalePopup.IsLive(sale, lastSale);
            var cursor = Cursor;
            return cursor != null && cursor.GetCurrentCursor != null && !cursor.IsGrabItem();
        }

        private void LateUpdate()
        {
            try
            {
                if (!Enabled || owner == null || !owner.isActiveAndEnabled || DialogManager.IsInvalid ||
                    DialogManager.GetTopDialog() != owner || owner.IsDisable || owner.IsDisableOperation)
                { Release(); return; }
                if (owner is ItemSaleDialog sale) { UpdateSale(sale); return; }
                var cursor = Cursor;
                if (cursor == null || cursor.GetCurrentCursor == null || cursor.IsGrabItem())
                { Hide(); return; }
                InventoryCellItemData cell = null;
                SaveItemList.ItemInfo repository = null;
                RectTransform anchor = null;
                InventoryBag bag = null;
                Vector2Int position = cursor.GetCurrentCursor.Position;

                // Moving into the description to wheel/drag its native ScrollRect must not
                // discard the hovered item. Retain it ONLY while the same live entry exists.
                if (MouseMode && PointerInside(root) && StillPresent())
                {
                    cell = lastCell; repository = lastRepository; anchor = lastAnchor;
                    bag = lastBag; position = lastPosition;
                }
                else if (owner is RepositoryUI repositoryUI &&
                    Value<RepositoryUI.CursorState>(owner, "currentCursorState") == RepositoryUI.CursorState.Repository)
                {
                    var tab = Value<RepositoryUI.TabType>(owner, "currentTabType");
                    var lists = repositoryUI.GetRepositoryItemList();
                    int index = Value<int>(owner, "repositoryCursorIdx");
                    if (lists.TryGetValue(tab, out var items))
                    {
                        var map = NativeDescriptions.Field<Dictionary<string, RepositoryElement>>(owner, "itemMap");
                        RepositoryElement row = null;
                        if (MouseMode)
                        {
                            // Vanilla's mouse callback FindIndex compares base itemID, so two
                            // artifact instances can leave the index on the wrong row. Read
                            // the actual hovered row and its instance ItemInfo instead.
                            foreach (var item in items)
                                if (map.TryGetValue(item.GetItemID, out var candidate) && candidate != null &&
                                    candidate.gameObject.activeInHierarchy && candidate.GetMouseHandler.IsOverlap)
                                { row = candidate; break; }
                        }
                        else if (index >= 0 && index < items.Count) map.TryGetValue(items[index].GetItemID, out row);
                        if (row != null && row.gameObject.activeInHierarchy && !row.GetMouseHandler.IsDrag)
                        {
                            repository = row.GetItemInfo(); anchor = row.GetRectTransform;
                        }
                    }
                }
                else
                {
                    bag = cursor.GetCurrentCursor.GetBag();
                    if (bag != null && position.x >= 0 && position.y >= 0 && position.x < bag.Width && position.y < bag.Height)
                        cell = bag.GetItem(position);
                    if (cell != null && (cell.isGrab || cell.inventoryItemUI == null ||
                        !cell.inventoryItemUI.gameObject.activeInHierarchy ||
                        (MouseMode && !cell.inventoryItemUI.GetMouseHandler.IsOverlap) ||
                        cell.inventoryItemUI.GetMouseHandler.IsDrag)) cell = null;
                    if (cell != null) anchor = cell.inventoryItemUI.GetRectTransform;
                }
                if ((cell == null && repository == null) || anchor == null) { Hide(); return; }
                if (renderer == null && !Acquire()) return;
                string id = cell != null ? cell.GetItemID : repository.GetItemID;
                int count = cell != null ? cell.currentStackCount : repository.num;
                bool changed = lastCell != cell || lastRepository != repository || lastId != id || lastCount != count ||
                    lastAnchor != anchor || lastAnchorPosition != anchor.position || lastAnchorSize != anchor.rect.size ||
                    lastScreenWidth != Screen.width || lastScreenHeight != Screen.height || !root.gameObject.activeSelf;
                lastCell = cell; lastRepository = repository; lastBag = bag; lastPosition = position;
                lastAnchor = anchor; lastId = id; lastCount = count;
                if (changed) Render(cell, repository, anchor);
                else Place(anchor); // live requirement refresh may have resized/reclamped it
                lastAnchorPosition = anchor.position; lastAnchorSize = anchor.rect.size;
                lastScreenWidth = Screen.width; lastScreenHeight = Screen.height;
            }
            catch (Exception e) { Release(); NativeDescriptions.Warn(e); }
        }

        private void UpdateSale(ItemSaleDialog sale)
        {
            // Keep a live hovered row while entering its description to wheel/drag. Do not
            // retain destroyed/rebuilt rows across tab/sort/staging/sale operations.
            var selection = MouseMode && PointerInside(root) && NativeWorkshopSalePopup.IsLive(sale, lastSale)
                ? lastSale : NativeWorkshopSalePopup.Select(sale, MouseMode);
            if (selection == null || selection.Anchor == null) { Hide(); return; }
            if (renderer == null && !Acquire()) return;
            var anchor = selection.Anchor;
            string id = selection.Id;
            int count = selection.Count;
            bool changed = !selection.SameEntry(lastSale) || lastId != id || lastCount != count ||
                lastAnchor != anchor || lastAnchorPosition != anchor.position || lastAnchorSize != anchor.rect.size ||
                lastScreenWidth != Screen.width || lastScreenHeight != Screen.height || !root.gameObject.activeSelf;
            lastSale = selection; lastAnchor = anchor; lastId = id; lastCount = count;
            if (changed) Render(null, null, anchor, selection);
            else Place(anchor);
            lastAnchorPosition = anchor.position; lastAnchorSize = anchor.rect.size;
            lastScreenWidth = Screen.width; lastScreenHeight = Screen.height;
        }

        private bool StillPresent()
        {
            if (lastAnchor == null || !lastAnchor.gameObject.activeInHierarchy) return false;
            if (lastCell != null)
                return lastBag != null && !lastCell.isGrab && lastBag.GetItem(lastPosition) == lastCell &&
                    lastCell.inventoryItemUI != null && !lastCell.inventoryItemUI.GetMouseHandler.IsDrag;
            if (lastRepository == null || !(owner is RepositoryUI repositoryUI)) return false;
            if (Value<RepositoryUI.CursorState>(owner, "currentCursorState") != RepositoryUI.CursorState.Repository) return false;
            var tab = Value<RepositoryUI.TabType>(owner, "currentTabType");
            return repositoryUI.GetRepositoryItemList().TryGetValue(tab, out var items) && items.Contains(lastRepository);
        }

        private static T Value<T>(object instance, string field) => (T)AccessTools.Field(instance.GetType(), field).GetValue(instance);
        private static void Set(object instance, string field, object value) => AccessTools.Field(instance.GetType(), field).SetValue(instance, value);

        private static bool Complete(ItemPopUp popup, DialogBase dialog)
        {
            if (popup == null) return false;
            var obj = NativeDescriptions.Field<GameObject>(popup, "m_InventoryPopUpObject");
            if (obj == null || !(obj.transform is RectTransform) || dialog.transform.IsChildOf(obj.transform)) return false;
            foreach (string field in new[] { "m_InventoryPopUpItemNameText", "m_InventoryPopUpItemDeteilText", "m_SallingPrice" })
            {
                var text = NativeDescriptions.Field<TextMeshProUGUI>(popup, field);
                if (text == null || !text.transform.IsChildOf(obj.transform)) return false;
            }
            return true;
        }

        private bool Acquire()
        {
            if (Time.unscaledTime < retryAt) return false;
            retryAt = Time.unscaledTime + 0.5f;
            // The real workshop sale dialog has no serialized ItemPopUp at all. Clone only
            // native backpack presentation below, without probing nonexistent legacy fields.
            originalPopup = owner is ItemSaleDialog ? null :
                NativeDescriptions.Field<ItemPopUp>(owner, owner is RepositoryUI ? "m_InventoryPopUp" : "m_PopUp");
            // Two pause tabs may serialize the same native object. Let its previous owner
            // release first; never acquire a second lease in an arbitrary LateUpdate order.
            if (NativeStoragePopups.IsManaged(originalPopup)) { originalPopup = null; return false; }
            ItemPopUp source = originalPopup;
            cloned = !Complete(source, owner);
            if (cloned)
            {
                source = null;
                // Do not instantiate an InventoryUI/menu or invent a substitute skin. Copy
                // ONLY a real backpack's native presentation subtree and serialized fields.
                foreach (var inventory in Resources.FindObjectsOfTypeAll<InventoryUI>())
                {
                    if (inventory == null || !inventory.gameObject.scene.IsValid()) continue;
                    var candidate = NativeDescriptions.Field<ItemPopUp>(inventory, "PopUp");
                    if (!Complete(candidate, owner) || NativeStoragePopups.IsManaged(candidate)) continue;
                    bool modified = false;
                    foreach (var binding in candidate.GetComponents<NativeMaterialDescription>())
                        if (binding.IsApplied) modified = true;
                    // An inactive backpack restores its description automatically. Never
                    // mutate its live scroll layout just to manufacture a clone.
                    if (modified) continue;
                    source = candidate; break;
                }
                if (source == null) return false; // retry when the real native object exists
            }
            canvas = owner.GetComponentInParent<Canvas>();
            if (canvas == null) canvas = SingletonMonoBehaviour<DialogManager>.Instance.GetDialogCanvas();
            if (canvas == null) return false;
            host = new GameObject("Wishlist native storage popup", typeof(RectTransform), typeof(ItemPopUp)).GetComponent<RectTransform>();
            host.SetParent(owner.transform, false);
            host.anchorMin = Vector2.zero; host.anchorMax = Vector2.one; host.offsetMin = host.offsetMax = Vector2.zero;
            host.SetAsLastSibling();
            renderer = host.GetComponent<ItemPopUp>();
            var sourceRoot = NativeDescriptions.Field<GameObject>(source, "m_InventoryPopUpObject").GetComponent<RectTransform>();
            if (!cloned) NativeDescriptions.Before(source);
            savedRoot = new RootState(sourceRoot);
            root = cloned ? UnityEngine.Object.Instantiate(sourceRoot, host, false) : sourceRoot;
            root.gameObject.SetActive(false);
            if (!cloned) root.SetParent(host, true);
            // Native root already contains its fonts, graphics, heading, price and layout.
            foreach (string field in new[] { "m_InventoryPopUpItemNameText", "m_InventoryPopUpItemDeteilText", "m_SallingPrice" })
            {
                var text = NativeDescriptions.Field<TextMeshProUGUI>(source, field);
                Set(renderer, field, cloned ? Equivalent(sourceRoot, root, text.transform).GetComponent<TextMeshProUGUI>() : text);
            }
            oldName = NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_InventoryPopUpItemNameText").text;
            oldDetail = NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_InventoryPopUpItemDeteilText").text;
            var price = NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_SallingPrice");
            oldPrice = price.text; oldPriceActive = price.gameObject.activeSelf;
            EnableTextPath(NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_InventoryPopUpItemNameText"));
            EnableTextPath(NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_InventoryPopUpItemDeteilText"));
            Set(renderer, "m_InventoryPopUpObject", root.gameObject);
            var unused = new GameObject("Unused native repository variant", typeof(RectTransform));
            unused.transform.SetParent(host, false); unused.SetActive(false);
            Set(renderer, "m_RepositoryPopUpObject", unused);
            foreach (string field in new[] { "PopUpWindowSpace", "m_UnderLineOffset", "PopUpTextSpace" })
                Set(renderer, field, Value<float>(source, field));
            renderer.SetPopUpCanvas(canvas);
            if (originalPopup != null)
            {
                if (cloned)
                {
                    NativeDescriptions.Before(originalPopup);
                    var unusedInventory = NativeDescriptions.Field<GameObject>(originalPopup, "m_InventoryPopUpObject");
                    if (unusedInventory != null) unusedInventory.SetActive(false);
                }
                repositoryRoot = NativeDescriptions.Field<GameObject>(originalPopup, "m_RepositoryPopUpObject");
                if (repositoryRoot != null) repositoryRoot.SetActive(false);
                NativeStoragePopups.Register(originalPopup, this);
            }
            NativeStoragePopups.Register(renderer, this);
            return true;
        }

        private void EnableTextPath(TextMeshProUGUI text)
        {
            nativeTextEnabled[text] = text.enabled;
            text.enabled = true;
            for (var node = text.transform; node != null && node != root; node = node.parent)
            {
                if (!nativeActive.ContainsKey(node.gameObject)) nativeActive[node.gameObject] = node.gameObject.activeSelf;
                node.gameObject.SetActive(true);
            }
        }

        private static Transform Equivalent(Transform sourceRoot, Transform cloneRoot, Transform original)
        {
            var indexes = new Stack<int>();
            for (var node = original; node != sourceRoot; node = node.parent) indexes.Push(node.GetSiblingIndex());
            var result = cloneRoot;
            while (indexes.Count != 0) result = result.GetChild(indexes.Pop());
            return result;
        }

        private void Render(InventoryCellItemData cell, SaveItemList.ItemInfo repository, RectTransform anchor,
            NativeWorkshopSalePopup.Selection sale = null)
        {
            Rendering = true;
            try
            {
                NativeDescriptions.Before(renderer);
                root.gameObject.SetActive(true);
                if (cell != null)
                {
                    // The exact vanilla backpack renderer, including native name, flavor,
                    // price and item-specific lookup. The existing append hook supplies goals.
                    renderer.SetPopUp(cell);
                }
                else
                {
                    // Repository rows have no InventoryCellItemData/UI. Render their actual
                    // ItemInfo into the FULL backpack variant rather than the flavor-only
                    // repository variant; never create a fake bag item to call SetPopUp.
                    // Sale summary rows supply their real staged GUID/base ID, not a fake
                    // InventoryCellItemData or a guessed label from an uninitialized row.
                    string itemId = sale != null ? sale.Id : repository.GetItemID;
                    string baseId = sale != null ? sale.BaseId : repository.itemID;
                    var info = SingletonMonoBehaviour<Inventory>.Instance.GetItemInfo(itemId);
                    if (info == null) { Hide(); return; }
                    NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_InventoryPopUpItemNameText").text = info.GetItemName();
                    var detail = NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_InventoryPopUpItemDeteilText");
                    detail.text = info.GetItemDeteil();
                    if (string.IsNullOrEmpty(detail.text)) detail.text = "FlavorText NotFound";
                    int priceValue = (int)CSVParams.instance.GetCSVParam(baseId, "SALLING_PRICE");
                    var price = NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_SallingPrice");
                    price.gameObject.SetActive(priceValue > 0);
                    price.text = priceValue + " " + StringManager.instance.GetString("ItemName", "MONEY");
                    Canvas.ForceUpdateCanvases();
                    root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                        detail.rectTransform.sizeDelta.y - detail.rectTransform.anchoredPosition.y + Value<float>(renderer, "PopUpTextSpace"));
                    NativeDescriptions.Popup(renderer, itemId, false);
                }
                Place(anchor);
            }
            finally { Rendering = false; }
        }

        private void Place(RectTransform anchor)
        {
            // Work in the canvas's coordinate system, not screen pixels or assumed 1080p.
            var bounds = canvas.rootCanvas.transform as RectTransform;
            if (bounds == null) return;
            var corners = new Vector3[4]; anchor.GetWorldCorners(corners);
            float anchorLeft = float.PositiveInfinity, anchorRight = float.NegativeInfinity, anchorTop = float.NegativeInfinity;
            foreach (var corner in corners)
            {
                Vector3 p = bounds.InverseTransformPoint(corner);
                anchorLeft = Mathf.Min(anchorLeft, p.x); anchorRight = Mathf.Max(anchorRight, p.x); anchorTop = Mathf.Max(anchorTop, p.y);
            }
            // Shrink only the popup transform if native chrome itself exceeds the viewport.
            // Long description text is independently scrollable through NativeDescriptions.
            root.localScale = savedRoot.Scale;
            root.GetWorldCorners(corners);
            float width = Vector3.Distance(bounds.InverseTransformPoint(corners[0]), bounds.InverseTransformPoint(corners[3]));
            float height = Vector3.Distance(bounds.InverseTransformPoint(corners[0]), bounds.InverseTransformPoint(corners[1]));
            Rect area = bounds.rect;
            float scale = Mathf.Min(1f, Mathf.Min((area.width - 16f) / Mathf.Max(1f, width), (area.height - 56f) / Mathf.Max(1f, height)));
            root.localScale *= Mathf.Max(0.01f, scale);
            width *= scale; height *= scale;
            float x = anchorRight + 10f;
            if (x + width > area.xMax - 8f) x = anchorLeft - width - 10f;
            x = Mathf.Clamp(x, area.xMin + 8f, Mathf.Max(area.xMin + 8f, area.xMax - width - 8f));
            float y = Mathf.Clamp(anchorTop, area.yMin + height + 40f, Mathf.Max(area.yMin + height + 40f, area.yMax - 8f));
            root.GetWorldCorners(corners);
            Vector3 currentTopLeft = bounds.InverseTransformPoint(corners[1]);
            root.position += bounds.TransformVector(new Vector3(x - currentTopLeft.x, y - currentTopLeft.y, 0));
        }

        private bool PointerInside(RectTransform rect)
        {
            var device = Device;
            if (rect == null || !rect.gameObject.activeInHierarchy || device == null || DeviceMouse == null) return false;
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, (Vector3)DeviceMouse.GetValue(device), camera);
        }

        private void Hide()
        {
            if (renderer != null) NativeDescriptions.Before(renderer);
            if (root != null) root.gameObject.SetActive(false);
            if (repositoryRoot != null) repositoryRoot.SetActive(false);
            // Even before a lease is acquired, never show vanilla's grabbed-item tooltip.
            var native = originalPopup != null ? originalPopup : (owner != null && !(owner is ItemSaleDialog) ?
                NativeDescriptions.Field<ItemPopUp>(owner, owner is RepositoryUI ? "m_InventoryPopUp" : "m_PopUp") : null);
            if (native != null)
            {
                var obj = NativeDescriptions.Field<GameObject>(native, "m_InventoryPopUpObject");
                if (obj != null) obj.SetActive(false);
            }
            lastCell = null; lastRepository = null; lastSale = null; lastAnchor = null; lastBag = null; lastId = null;
        }

        internal void Release()
        {
            if (renderer != null) NativeDescriptions.Before(renderer);
            if (root != null)
            {
                root.gameObject.SetActive(false);
                if (!cloned)
                {
                    if (renderer != null)
                    {
                        NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_InventoryPopUpItemNameText").text = oldName;
                        NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_InventoryPopUpItemDeteilText").text = oldDetail;
                        var price = NativeDescriptions.Field<TextMeshProUGUI>(renderer, "m_SallingPrice");
                        price.text = oldPrice; price.gameObject.SetActive(oldPriceActive);
                    }
                    foreach (var pair in nativeTextEnabled) if (pair.Key != null) pair.Key.enabled = pair.Value;
                    foreach (var pair in nativeActive) if (pair.Key != null) pair.Key.SetActive(pair.Value);
                    savedRoot.Restore(root);
                }
            }
            nativeTextEnabled.Clear(); nativeActive.Clear();
            if (repositoryRoot != null) repositoryRoot.SetActive(false);
            NativeStoragePopups.Unregister(originalPopup); NativeStoragePopups.Unregister(renderer);
            if (host != null) { host.gameObject.SetActive(false); Destroy(host.gameObject); }
            renderer = null; originalPopup = null; host = null; root = null; repositoryRoot = null;
            lastCell = null; lastRepository = null; lastSale = null; lastAnchor = null; lastBag = null; lastId = null;
            Rendering = false;
        }
        private void OnDisable() { Release(); }
        private void OnDestroy() { Release(); }
    }
}
