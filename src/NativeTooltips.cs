using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace AnomalithWishlist
{
    // Patch renderers, never Inventory.GetItemDeteilText / StringManager / item names.
    // Prefixes undo our geometry BEFORE vanilla measures/reuses a pooled description.
    [HarmonyPatch(typeof(ItemPopUp), nameof(ItemPopUp.SetPopUp), new[] { typeof(InventoryCellItemData) })]
    internal static class NativeInventoryDescriptionPatch
    {
        private static bool Prefix(ItemPopUp __instance)
        {
            if (!NativeStoragePopups.AllowNativeWrite(__instance)) return false;
            NativeDescriptions.Before(__instance);
            return true;
        }
        private static void Postfix(ItemPopUp __instance, InventoryCellItemData itemData, bool __runOriginal)
        {
            if (__runOriginal) NativeDescriptions.Popup(__instance, itemData?.GetItemID, false);
        }
    }

    [HarmonyPatch(typeof(ItemPopUp), nameof(ItemPopUp.SetPopUp), new[] { typeof(SaveItemList.ItemInfo), typeof(RectTransform) })]
    internal static class NativeRepositoryDescriptionPatch
    {
        private static bool Prefix(ItemPopUp __instance)
        {
            if (!NativeStoragePopups.AllowNativeWrite(__instance)) return false;
            NativeDescriptions.Before(__instance);
            return true;
        }
        private static void Postfix(ItemPopUp __instance, SaveItemList.ItemInfo itemData, bool __runOriginal)
        {
            if (__runOriginal) NativeDescriptions.Popup(__instance, itemData?.GetItemID, true);
        }
    }

    [HarmonyPatch(typeof(InventoryUI), "ItemTextUpdate")]
    internal static class NativeInventoryPanelPatch
    {
        private static void Prefix(InventoryUI __instance) => NativeDescriptions.Before(__instance);
        private static void Postfix(InventoryUI __instance, InventoryCellItemData itemData)
        {
            NativeDescriptions.Panel(__instance, "itemDeteilText", itemData?.GetItemID);
        }
    }

    [HarmonyPatch(typeof(ItemDeteilWindow), nameof(ItemDeteilWindow.SetItem))]
    internal static class NativeSelectedItemDescriptionPatch
    {
        private static void Prefix(ItemDeteilWindow __instance) => NativeDescriptions.Before(__instance);
        private static void Postfix(ItemDeteilWindow __instance, string itemID)
        {
            NativeDescriptions.Panel(__instance, "deteil", itemID);
        }
    }

    // SetText is also public: an unrelated blank/placeholder must not retain the previous item.
    [HarmonyPatch(typeof(ItemDeteilWindow), nameof(ItemDeteilWindow.SetText))]
    internal static class NativeSelectedItemClearPatch
    {
        private static void Prefix(ItemDeteilWindow __instance) => NativeDescriptions.Before(__instance);
    }

    [HarmonyPatch(typeof(DevelopmetInfo), nameof(DevelopmetInfo.SetInfoText))]
    internal static class NativeWorkshopDescriptionPatch
    {
        private static void Prefix(DevelopmetInfo __instance) => NativeDescriptions.Before(__instance);
        private static void Postfix(DevelopmetInfo __instance, DevelopmetData data)
        {
            NativeDescriptions.Panel(__instance, "m_InfoText", data?.m_ItemId);
        }
    }

    // SetInfoData may replace the description with ????? without invoking SetInfoText.
    // Do NOT append in this postfix: only SetInfoText proves that vanilla revealed the item.
    [HarmonyPatch(typeof(DevelopmetInfo), nameof(DevelopmetInfo.SetInfoData))]
    internal static class NativeWorkshopSelectionPatch
    {
        private static void Prefix(DevelopmetInfo __instance) => NativeDescriptions.Before(__instance);
    }

    [HarmonyPatch(typeof(DevelopmetInfo), nameof(DevelopmetInfo.SetNoInfoText))]
    internal static class NativeWorkshopClearPatch
    {
        private static void Prefix(DevelopmetInfo __instance) => NativeDescriptions.Before(__instance);
    }

    internal static class NativeDescriptions
    {
        private static readonly Dictionary<string, FieldInfo> Fields = new Dictionary<string, FieldInfo>();
        private static readonly HashSet<string> Warned = new HashSet<string>();

        internal static T Field<T>(Component owner, string name) where T : class
        {
            string key = owner.GetType().FullName + "." + name;
            if (!Fields.TryGetValue(key, out var field))
            {
                field = AccessTools.Field(owner.GetType(), name);
                Fields[key] = field;
            }
            if (field == null) throw new MissingFieldException(key);
            return field.GetValue(owner) as T;
        }

        internal static void Warn(Exception e)
        {
            string message = e.GetType().Name + ": " + e.Message;
            if (Warned.Add(message)) Debug.LogWarning("Wishlist native description: " + message);
        }

        internal static void Before(Component owner)
        {
            try
            {
                if (owner == null) return;
                foreach (var binding in owner.GetComponents<NativeMaterialDescription>()) binding.BeforeNative();
            }
            catch (Exception e) { Warn(e); }
        }

        internal static bool IsShopDescription(Component owner)
        {
            // The actual workshop sale popup is parented to ItemSaleDialog; SaleMenu
            // is a separate legacy screen. Do not use the global top dialog, which
            // would also relabel an unrelated cached backpack/warehouse description.
            return owner != null && (owner.GetComponentInParent<ItemSaleDialog>() != null ||
                owner.GetComponentInParent<SaleMenu>() != null || owner.GetComponentInParent<DevMenuBase>() != null ||
                owner.GetComponent<DevelopmetInfo>() != null);
        }

        internal static void Popup(ItemPopUp owner, string id, bool repository)
        {
            try
            {
                string prefix = repository ? "m_RepositoryPopUp" : "m_InventoryPopUp";
                var text = Field<TextMeshProUGUI>(owner, prefix + "ItemDeteilText");
                var obj = Field<GameObject>(owner, prefix + "Object");
                Attach(owner, text, id, obj != null ? obj.GetComponent<RectTransform>() : null,
                    Field<Canvas>(owner, "PopUpCanvas"));
            }
            catch (Exception e) { Warn(e); }
        }

        internal static void Panel(Component owner, string field, string id)
        {
            try { Attach(owner, Field<TextMeshProUGUI>(owner, field), id, null, null); }
            catch (Exception e) { Warn(e); }
        }

        private static void Attach(Component owner, TextMeshProUGUI text, string id, RectTransform popup, Canvas canvas)
        {
            if (text == null) return;
            NativeMaterialDescription target = null;
            foreach (var b in owner.GetComponents<NativeMaterialDescription>())
                if (b.NativeText == text) { target = b; break; }
            if (target == null)
            {
                if (string.IsNullOrEmpty(id)) return;
                target = owner.gameObject.AddComponent<NativeMaterialDescription>();
            }
            target.Bind(text, id, popup, canvas, owner is ItemPopUp itemPopup && NativeStoragePopups.IsManaged(itemPopup));
        }

        // Optional immediate cleanup for Plugin.OnDisable/OnDestroy; bindings also self-restore
        // when FeatureEnabled/Ready becomes false, without needing a new native selection event.
        internal static void RestoreAll()
        {
            foreach (var binding in Resources.FindObjectsOfTypeAll<NativeMaterialDescription>())
                if (binding != null) binding.Restore();
        }
    }

    // One binding per native description, on its OWNER (not on reparented text).
    // No replacement font, copied item data, free-floating window, or truncated goal list.
    public sealed class NativeMaterialDescription : MonoBehaviour
    {
        internal TextMeshProUGUI NativeText { get; private set; }
        private string itemId, original, rendered, suffix;
        private RectTransform popup, nativeParent, shell, viewport, handle;
        private Canvas canvas;
        private ScrollRect scroll;
        private Scrollbar scrollbar;
        private LayoutElement shellLayout;
        private ContentSizeFitter fitter;
        private bool fitterEnabled, applied, changing, captured, keepNativeScrollable;
        internal bool IsApplied => applied;
        private bool oldAutoSize, oldRaycast;
        private TextWrappingModes oldWrap;
        private TextOverflowModes oldOverflow;
        private TextAlignmentOptions oldAlignment;
        private int oldMaxCharacters, oldMaxLines, oldMaxWords;
        private float oldFontSize, baselineHeight, baselineWidth, nextRefresh, savedScroll = 1f;
        private Vector2 popupSize;
        private Vector3 popupPosition;
        private RectState nativeRect;
        private int nativeSibling, screenWidth, screenHeight;
        private string previousId;

        private struct RectState
        {
            internal Vector2 Min, Max, Pivot, Size;
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
            internal RectState(RectTransform r)
            {
                Min = r.anchorMin; Max = r.anchorMax; Pivot = r.pivot; Size = r.sizeDelta;
                Position = r.anchoredPosition3D; Scale = r.localScale; Rotation = r.localRotation;
            }
            internal void Apply(RectTransform r)
            {
                r.anchorMin = Min; r.anchorMax = Max; r.pivot = Pivot; r.sizeDelta = Size;
                r.anchoredPosition3D = Position; r.localScale = Scale; r.localRotation = Rotation;
            }
        }

        internal void BeforeNative()
        {
            Restore();
            itemId = null;
            captured = false;
        }

        internal void Bind(TextMeshProUGUI text, string id, RectTransform root, Canvas targetCanvas, bool nativeScrollable = false)
        {
            Restore();
            keepNativeScrollable = nativeScrollable;
            NativeText = text; itemId = id; popup = root;
            canvas = targetCanvas != null ? targetCanvas : text.GetComponentInParent<Canvas>();
            if (previousId != id) savedScroll = 1f;
            previousId = id;
            // Native selected-info setters do not all flush their ContentSizeFitter before
            // returning; capture the ORIGINAL layout, not the previous item's dimensions.
            Canvas.ForceUpdateCanvases();
            Capture();
            Refresh();
        }

        private void Capture()
        {
            if (NativeText == null) return;
            var r = NativeText.rectTransform;
            nativeParent = r.parent as RectTransform;
            if (nativeParent == null) return;
            nativeRect = new RectState(r); nativeSibling = r.GetSiblingIndex();
            original = NativeText.text ?? "";
            baselineHeight = Mathf.Max(1, r.rect.height);
            baselineWidth = Mathf.Max(1, r.rect.width);
            oldAutoSize = NativeText.enableAutoSizing; oldFontSize = NativeText.fontSize;
            oldWrap = NativeText.textWrappingMode; oldOverflow = NativeText.overflowMode;
            oldRaycast = NativeText.raycastTarget; oldAlignment = NativeText.alignment;
            oldMaxCharacters = NativeText.maxVisibleCharacters; oldMaxLines = NativeText.maxVisibleLines;
            oldMaxWords = NativeText.maxVisibleWords;
            fitter = NativeText.GetComponent<ContentSizeFitter>();
            fitterEnabled = fitter != null && fitter.enabled;
            if (popup != null) { popupSize = popup.sizeDelta; popupPosition = popup.localPosition; }
            captured = true;
        }

        private void LateUpdate()
        {
            try
            {
                if (changing || NativeText == null) return;
                if (Time.unscaledTime >= nextRefresh)
                {
                    nextRefresh = Time.unscaledTime + 0.2f;
                    Refresh();
                }
                ScrollWithKeys();
            }
            catch (Exception e) { Restore(); NativeDescriptions.Warn(e); }
        }

        private void Refresh()
        {
            if (changing || NativeText == null || !captured) return;
            var plugin = Plugin.Self;
            string addition = plugin != null && plugin.FeatureEnabled && plugin.Ready && !string.IsNullOrEmpty(itemId)
                ? plugin.MaterialText(itemId, NativeText.richText, NativeDescriptions.IsShopDescription(this)) ?? "" : "";
            bool nativeOnly = keepNativeScrollable && plugin != null && plugin.FeatureEnabled && plugin.Ready;
            if (string.IsNullOrEmpty(addition) && !nativeOnly) { Restore(); return; }
            // Plain MaterialText is also used for standalone native labels and intentionally
            // has no separator. Here it is a description suffix even on non-rich TMP fields.
            if (!NativeText.richText && !string.IsNullOrEmpty(addition)) addition = "\n\n" + addition;
            if (!NativeText.gameObject.activeInHierarchy) { Restore(); return; }
            // If an unpatched native writer replaced the field, its new text wins. Never append
            // a stale selected item's suffix to an unknown placeholder/item.
            if ((applied && NativeText.text != rendered) || (!applied && NativeText.text != original))
            {
                string replacement = NativeText.text;
                Restore(); NativeText.text = replacement; itemId = null; captured = false;
                return;
            }
            if (applied && addition == suffix && screenWidth == Screen.width && screenHeight == Screen.height
                && Mathf.Abs(shell.rect.width - baselineWidth) < 1f) return;
            Restore();
            Capture();
            suffix = addition;
            Apply();
        }

        private void Apply()
        {
            if (!captured || nativeParent == null) return;
            changing = true;
            // Mark first so an exception midway is still fully reversible.
            applied = true;
            try
            {
                EnsureScroll();
                var r = NativeText.rectTransform;
                shell.SetParent(nativeParent, false); nativeRect.Apply(shell); shell.SetSiblingIndex(nativeSibling);
                shell.gameObject.SetActive(true);
                if (fitter != null) fitter.enabled = false;
                NativeText.enableAutoSizing = false; // keep the original readable native font size
                NativeText.fontSize = oldFontSize; NativeText.textWrappingMode = TextWrappingModes.Normal;
                NativeText.overflowMode = TextOverflowModes.Overflow;
                NativeText.maxVisibleCharacters = int.MaxValue;
                NativeText.maxVisibleLines = int.MaxValue; NativeText.maxVisibleWords = int.MaxValue;
                // Preserve horizontal alignment, move vertical alignment to top for scrolling.
                NativeText.alignment = (TextAlignmentOptions)(((int)oldAlignment & 0xFF) | 0x100);
                rendered = original + suffix; NativeText.text = rendered;
                r.SetParent(viewport, false);
                r.anchorMin = new Vector2(0, 1); r.anchorMax = Vector2.one; r.pivot = new Vector2(0.5f, 1);
                r.localScale = Vector3.one; r.localRotation = Quaternion.identity;
                r.anchoredPosition3D = Vector3.zero;
                float width = baselineWidth;
                float naturalHeight = Measure(width);
                float availableHeight = baselineHeight;
                if (popup != null)
                {
                    float maxRootHeight = CanvasHeightIn(popup) - 56f;
                    float chrome = Mathf.Max(0, popup.rect.height - baselineHeight);
                    availableHeight = Mathf.Max(1, Mathf.Min(naturalHeight, maxRootHeight - chrome));
                    // Anchor the old top edge: growing a centred root must not move the heading.
                    float growth = availableHeight - baselineHeight;
                    popup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, popup.rect.height + growth);
                    popup.localPosition += Vector3.down * (growth * (1f - popup.pivot.y));
                }
                else
                {
                    // Fixed selected-item panels must not cover price, requirements or buttons.
                    // The existing description rectangle is the viewport; no unrelated UI moves.
                    availableHeight = Mathf.Min(baselineHeight, CanvasHeightIn(r) - 40f);
                    availableHeight = Mathf.Max(1, availableHeight);
                }
                bool overflow = naturalHeight > availableHeight + 1;
                viewport.offsetMin = Vector2.zero;
                viewport.offsetMax = new Vector2(overflow ? -12f : 0, 0);
                if (overflow) naturalHeight = Measure(Mathf.Max(1, width - 12f));
                shell.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, availableHeight);
                shellLayout.minHeight = availableHeight; shellLayout.preferredHeight = availableHeight;
                shellLayout.flexibleHeight = 0;
                r.sizeDelta = new Vector2(0, Mathf.Max(availableHeight, naturalHeight));
                NativeText.raycastTarget = overflow;
                scrollbar.gameObject.SetActive(overflow);
                scroll.vertical = overflow; scroll.enabled = overflow;
                scroll.StopMovement();
                LayoutRebuilder.ForceRebuildLayoutImmediate(shell);
                NativeText.ForceMeshUpdate();
                scroll.verticalNormalizedPosition = overflow ? savedScroll : 1f;
                if (popup != null) ClampPopup();
                screenWidth = Screen.width; screenHeight = Screen.height;
            }
            catch
            {
                changing = false; Restore(); throw;
            }
            finally { changing = false; }
        }

        private float Measure(float width)
        {
            return Mathf.Max(1, NativeText.GetPreferredValues(rendered, Mathf.Max(1, width), Mathf.Infinity).y + 3f);
        }

        private void EnsureScroll()
        {
            if (shell != null) return;
            var go = new GameObject("Wishlist native description scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
            shell = go.GetComponent<RectTransform>();
            go.GetComponent<Image>().color = Color.clear;
            shellLayout = go.GetComponent<LayoutElement>();
            var sourceLayout = NativeText.GetComponent<LayoutElement>();
            shellLayout.ignoreLayout = sourceLayout != null && sourceLayout.ignoreLayout;
            shellLayout.minWidth = LayoutUtility.GetMinWidth(NativeText.rectTransform);
            shellLayout.preferredWidth = LayoutUtility.GetPreferredWidth(NativeText.rectTransform);
            shellLayout.flexibleWidth = LayoutUtility.GetFlexibleWidth(NativeText.rectTransform);
            viewport = NewRect("Viewport", shell);
            Stretch(viewport); viewport.gameObject.AddComponent<RectMask2D>();
            scroll = go.GetComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true; scroll.viewport = viewport;
            scroll.content = NativeText.rectTransform; scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false; scroll.scrollSensitivity = Mathf.Max(24, oldFontSize * 2);
            var bar = NewRect("Scroll (wheel / PageUp PageDown / right stick)", shell);
            bar.anchorMin = new Vector2(1, 0); bar.anchorMax = Vector2.one; bar.pivot = new Vector2(1, 0.5f);
            bar.sizeDelta = new Vector2(7, 0); bar.anchoredPosition = Vector2.zero;
            var track = bar.gameObject.AddComponent<Image>(); track.color = new Color(1, 1, 1, 0.12f);
            scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            handle = NewRect("Handle", bar); Stretch(handle);
            var image = handle.gameObject.AddComponent<Image>();
            Color color = NativeText.color; color.a = 0.65f; image.color = color;
            scrollbar.handleRect = handle; scrollbar.targetGraphic = image;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); return r;
        }

        private static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero;
        }

        private float CanvasHeightIn(RectTransform r)
        {
            var c = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
            if (c == null) return Screen.height / Mathf.Max(0.01f, Mathf.Abs(r.lossyScale.y));
            return c.rect.height * Mathf.Abs(c.lossyScale.y) / Mathf.Max(0.01f, Mathf.Abs(r.lossyScale.y));
        }

        private void ClampPopup()
        {
            var c = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
            if (popup == null || c == null) return;
            var corners = new Vector3[4]; popup.GetWorldCorners(corners);
            float left = float.PositiveInfinity, right = float.NegativeInfinity;
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            foreach (var corner in corners)
            {
                Vector3 v = c.InverseTransformPoint(corner);
                left = Mathf.Min(left, v.x); right = Mathf.Max(right, v.x);
                bottom = Mathf.Min(bottom, v.y); top = Mathf.Max(top, v.y);
            }
            Rect bounds = c.rect;
            float dx = left < bounds.xMin + 6 ? bounds.xMin + 6 - left : 0;
            if (right + dx > bounds.xMax - 6) dx += bounds.xMax - 6 - right - dx;
            float dy = bottom < bounds.yMin + 40 ? bounds.yMin + 40 - bottom : 0;
            if (top + dy > bounds.yMax - 6) dy += bounds.yMax - 6 - top - dy;
            popup.position += c.TransformVector(new Vector3(dx, dy, 0));
        }

        private void ScrollWithKeys()
        {
            if (!applied || scroll == null || !scroll.enabled || !NativeText.gameObject.activeInHierarchy) return;
            if (!Application.isFocused || !OwnsInput()) return;
            // Never steal left/right, submit, cancel, or list-navigation inputs.
            // Mouse wheel/drag uses ordinary Unity ScrollRect events. These are additive readers.
            float pixels = 0;
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.pageDownKey.isPressed) pixels -= 350f * Time.unscaledDeltaTime;
                if (keyboard.pageUpKey.isPressed) pixels += 350f * Time.unscaledDeltaTime;
            }
            var pad = Gamepad.current;
            if (pad != null)
            {
                float axis = pad.rightStick.ReadValue().y;
                if (Mathf.Abs(axis) > 0.25f) pixels += axis * 300f * Time.unscaledDeltaTime;
            }
            float range = scroll.content.rect.height - viewport.rect.height;
            if (range > 1 && pixels != 0)
                scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition + pixels / range);
        }

        private bool OwnsInput()
        {
            if (DialogManager.IsInvalid) return false;
            var top = DialogManager.GetTopDialog();
            if (top == null || !top.isActiveAndEnabled || top.IsDisable) return false;
            var managedPopup = GetComponent<ItemPopUp>();
            // Managed ItemSaleDialog popups have no inventory cursor or serialized m_PopUp.
            // Ask their exact row owner before the generic parent/native-field fallbacks;
            // this also prevents input leaking into quantity/confirmation/tutorial dialogs.
            if (managedPopup != null && NativeStoragePopups.IsManaged(managedPopup))
                return NativeStoragePopups.OwnsInput(managedPopup, top);
            if (GetComponentInParent<DialogBase>() == top) return true;
            // Native popup/detail objects can live under a shared Canvas, not their dialog.
            // Resolve the EXACT serialized reference of the current top inventory dialog;
            // merely finding an inventory dialog anywhere would leak input through confirms.
            var nativePopup = GetComponent<ItemPopUp>();
            if (nativePopup != null)
            {
                if (NativeStoragePopups.IsManaged(nativePopup)) return NativeStoragePopups.OwnsInput(nativePopup, top);
                if (top is InventoryUI) return NativeDescriptions.Field<ItemPopUp>(top, "PopUp") == nativePopup;
                if (top is RepositoryUI) return NativeDescriptions.Field<ItemPopUp>(top, "m_InventoryPopUp") == nativePopup;
                if (top is StorageMenu || top is SaleMenu)
                    return NativeDescriptions.Field<ItemPopUp>(top, "m_PopUp") == nativePopup;
                return false;
            }
            var detail = GetComponent<ItemDeteilWindow>();
            if (detail != null)
                return (top is InventoryUI || top is RepositoryUI) &&
                    NativeDescriptions.Field<ItemDeteilWindow>(top, "deteilWindow") == detail;
            var workshop = GetComponent<DevelopmetInfo>();
            return workshop != null && top is DevMenuBase menu && GameRecipes.IsRecipeMenu(menu) &&
                NativeDescriptions.Field<DevelopmetInfo>(menu, "m_Info") == workshop;
        }

        internal void Restore()
        {
            if (!applied || changing) return;
            changing = true;
            try
            {
                if (scroll != null && scroll.enabled) savedScroll = Mathf.Clamp01(scroll.verticalNormalizedPosition);
                if (NativeText != null)
                {
                    if (NativeText.text == rendered) NativeText.text = original;
                    NativeText.enableAutoSizing = oldAutoSize; NativeText.fontSize = oldFontSize;
                    NativeText.textWrappingMode = oldWrap; NativeText.overflowMode = oldOverflow;
                    NativeText.raycastTarget = oldRaycast; NativeText.alignment = oldAlignment;
                    NativeText.maxVisibleCharacters = oldMaxCharacters;
                    NativeText.maxVisibleLines = oldMaxLines; NativeText.maxVisibleWords = oldMaxWords;
                    if (nativeParent != null)
                    {
                        NativeText.rectTransform.SetParent(nativeParent, false);
                        nativeRect.Apply(NativeText.rectTransform);
                        NativeText.rectTransform.SetSiblingIndex(nativeSibling);
                    }
                    if (fitter != null) fitter.enabled = fitterEnabled;
                }
                if (shell != null) shell.gameObject.SetActive(false);
                if (popup != null) { popup.sizeDelta = popupSize; popup.localPosition = popupPosition; }
                applied = false; suffix = null; rendered = null;
            }
            finally { changing = false; }
        }

        private void OnDisable() { Restore(); }
        private void OnDestroy()
        {
            Restore();
            if (shell != null) Destroy(shell.gameObject);
        }
    }
}
