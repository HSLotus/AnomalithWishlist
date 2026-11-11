using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnomalithWishlist
{
    // Observers only: no input interception, standalone canvas, raw icon changes or
    // replacement of native guides. Native objects own the added children/components.
    internal static class NativeWishlistUI
    {
        internal static readonly Color ShortColor = new Color32(255, 214, 92, 255);
        internal static readonly Color StockedColor = new Color32(120, 224, 165, 255);
        private static readonly HashSet<WishlistBadgeBinding> Badges = new HashSet<WishlistBadgeBinding>();
        private static readonly HashSet<WishlistGuideBinding> Guides = new HashSet<WishlistGuideBinding>();
        private static readonly HashSet<StorageMenu> StorageMenus = new HashSet<StorageMenu>();
        private static readonly FieldInfo Warehouse = AccessTools.Field(typeof(StorageMenu), "m_StorageBag");
        private static readonly FieldInfo ReinforceWeapon = AccessTools.Field(typeof(WeaponReinforceSelector), "m_TargetWeaponId");
        private static readonly FieldInfo GuideField = AccessTools.Field(typeof(DevelopmentMenu), "m_MenuButtonGuide");
        internal static readonly FieldInfo GuideTextField = AccessTools.Field(typeof(ButtonGuideText), "m_Text");
        private static bool reportedError;

        internal static void Report(Exception error)
        {
            if (reportedError) return;
            reportedError = true;
            Debug.LogWarning("[ANOMALITH Wishlist] Native UI observer stopped safely: " + error);
        }

        private static void Bind(Component owner)
        {
            if (owner == null) return;
            try
            {
                var binding = owner.GetComponent<WishlistBadgeBinding>();
                if (binding == null) binding = owner.gameObject.AddComponent<WishlistBadgeBinding>();
                binding.Owner = owner;
                binding.enabled = true;
                binding.Invalidate();
                Badges.Add(binding);
            }
            catch (Exception e) { Report(e); }
        }

        private static void BindGuide(DevelopmentMenu menu)
        {
            if (menu == null) return;
            try
            {
                var native = (MenuButtonGuide)GuideField.GetValue(menu);
                if (native == null) return;
                var binding = menu.GetComponent<WishlistGuideBinding>();
                if (binding == null) binding = menu.gameObject.AddComponent<WishlistGuideBinding>();
                binding.Guide = native;
                Guides.Add(binding);
            }
            catch (Exception e) { Report(e); }
        }

        internal static void Forget(WishlistBadgeBinding badge) => Badges.Remove(badge);
        internal static void Forget(WishlistGuideBinding guide) => Guides.Remove(guide);

        internal static bool IsRealMaterialTile(InventoryItem tile, InventoryCellItemData data)
        {
            if (data == null || !ReferenceEquals(data.inventoryItemUI, tile)) return false;
            // Predictions copy currentBag, so bag equality alone is NOT proof of ownership.
            if (StockReader.IsPlayerItem(data)) return true;
            foreach (var menu in StorageMenus)
            {
                if (menu == null) continue;
                var bag = Warehouse.GetValue(menu) as InventoryBag;
                if (bag == null || !ReferenceEquals(data.currentBag, bag)) continue;
                foreach (var member in bag.GetItemList())
                    if (ReferenceEquals(member, data)) return true;
            }
            return false;
        }

        internal static string TargetKey(DevelopItemSelector selector)
        {
            // Use the NEAREST menu: reinforcement can itself be nested under the
            // weapon-customization menu and must not be excluded with its ancestor.
            var menu = selector.GetComponentInParent<DevMenuBase>();
            if (!GameRecipes.IsRecipeMenu(menu)) return null;
            var data = selector.GetDevData();
            if (data == null) return null;
            string weapon = null;
            if (data is WeaponParamReinforceData parameter)
            {
                if (selector is WeaponReinforceSelector reinforce)
                    weapon = ReinforceWeapon.GetValue(reinforce) as string;
                if (string.IsNullOrEmpty(weapon)) weapon = (menu as WeaponReinforceMenu)?.GetWeaponDevData()?.m_ItemId;
                if (string.IsNullOrEmpty(weapon)) weapon = parameter.m_WeaponId;
                if (string.IsNullOrEmpty(weapon)) return null;
            }
            // Never substitute a completed previous stage when a native row advances.
            // This is the same exact-stage key that the current-selection input toggles.
            return GameRecipes.StageKey(data, weapon);
        }

        internal static void HideAll()
        {
            foreach (var badge in Badges) if (badge != null) badge.Hide();
            foreach (var guide in Guides) if (guide != null) guide.Hide();
        }

        internal static void Shutdown()
        {
            HideAll();
            foreach (var badge in new List<WishlistBadgeBinding>(Badges))
                if (badge != null) { badge.Release(); UnityEngine.Object.Destroy(badge); }
            foreach (var guide in new List<WishlistGuideBinding>(Guides))
                if (guide != null) { guide.Release(); UnityEngine.Object.Destroy(guide); }
            Badges.Clear();
            Guides.Clear();
            StorageMenus.Clear();
        }

        [HarmonyPatch(typeof(DevelopItemSelector), nameof(DevelopItemSelector.SetDevelopmentItemData))]
        private static class DevelopPatch
        {
            private static void Postfix(DevelopItemSelector __instance) => Bind(__instance);
        }

        [HarmonyPatch(typeof(SkillTreePartsBace), nameof(SkillTreePartsBace.SetSkillData))]
        private static class SkillPatch
        {
            private static void Postfix(SkillTreePartsBace __instance) => Bind(__instance);
        }

        [HarmonyPatch(typeof(InventoryItem), nameof(InventoryItem.Initialize))]
        private static class InventoryPatch
        {
            private static void Postfix(InventoryItem __instance) => Bind(__instance);
        }

        [HarmonyPatch(typeof(RepositoryElement), nameof(RepositoryElement.SetItemData))]
        private static class RepositoryPatch
        {
            private static void Postfix(RepositoryElement __instance) => Bind(__instance);
        }

        [HarmonyPatch(typeof(StorageMenu), nameof(StorageMenu.Initialize))]
        private static class StoragePatch
        {
            private static void Postfix(StorageMenu __instance)
            {
                StorageMenus.RemoveWhere(menu => menu == null);
                StorageMenus.Add(__instance);
            }
        }

        [HarmonyPatch(typeof(StorageMenu), nameof(StorageMenu.Destroy))]
        private static class StorageDestroyPatch
        {
            private static void Prefix(StorageMenu __instance) => StorageMenus.Remove(__instance);
        }

        [HarmonyPatch(typeof(DevelopmentMenu), nameof(DevelopmentMenu.Open))]
        private static class DevelopmentOpenPatch
        {
            private static void Postfix(DevelopmentMenu __instance) => BindGuide(__instance);
        }

        [HarmonyPatch(typeof(DevelopmentMenu), nameof(DevelopmentMenu.SetGuideText))]
        private static class DevelopmentGuidePatch
        {
            private static void Postfix(DevelopmentMenu __instance) => BindGuide(__instance);
        }
    }

    internal sealed class WishlistBadgeBinding : MonoBehaviour
    {
        internal Component Owner;
        private RectTransform badge, anchor;
        private WishlistVectorGlyph glyph;
        private readonly Vector3[] corners = new Vector3[4];
        private float nextRefresh;
        private int marker;

        internal void Invalidate() => nextRefresh = 0;
        internal void Hide()
        {
            if (badge != null) badge.gameObject.SetActive(false);
            marker = 0;
        }
        internal void Release()
        {
            enabled = false;
            Hide();
            if (badge != null) Destroy(badge.gameObject);
            badge = null;
            glyph = null;
        }
        private void OnDisable() => Hide();
        private void OnDestroy() { Release(); NativeWishlistUI.Forget(this); }

        private void LateUpdate()
        {
            var plugin = Plugin.Self;
            if (Owner == null || plugin == null || !plugin.FeatureEnabled || !plugin.Ready)
            { Hide(); return; }
            try
            {
                if (Time.unscaledTime >= nextRefresh)
                {
                    nextRefresh = Time.unscaledTime + 0.15f;
                    Refresh(plugin);
                }
                if (marker != 0 && badge != null && anchor != null)
                {
                    // Inventory rotates whole RectTransforms. Keep the badge upright on
                    // the SCREEN upper-right, rather than rotating it into another corner.
                    anchor.GetWorldCorners(corners);
                    float x = corners[0].x, y = corners[0].y;
                    for (int i = 1; i < 4; ++i) { x = Mathf.Max(x, corners[i].x); y = Mathf.Max(y, corners[i].y); }
                    badge.position = new Vector3(x, y, corners[0].z);
                    badge.rotation = Quaternion.identity;
                }
            }
            catch (Exception e)
            {
                Hide();
                enabled = false;
                NativeWishlistUI.Report(e);
            }
        }

        private void Refresh(Plugin plugin)
        {
            int value = 0;
            bool target = false;
            RectTransform bounds = Owner.transform as RectTransform;
            if (Owner is InventoryItem item)
            {
                var data = item.GetItemData();
                value = data == null ? 0 : plugin.MaterialMarker(data.GetItemID);
                if (value != 0 && !NativeWishlistUI.IsRealMaterialTile(item, data)) value = 0;
                // The native drag preview reparents its icon separately. Do not put a
                // badge on the placement prediction; it returns on the actual released tile.
                if (data != null && data.isGrab) value = 0;
                bounds = item.GetRectTransform;
            }
            else if (Owner is RepositoryElement repository)
                value = plugin.MaterialMarker(repository.GetInfo()?.GetItemID);
            else if (Owner is DevelopItemSelector selector)
            {
                target = true;
                value = plugin.TargetMarker(NativeWishlistUI.TargetKey(selector));
            }
            else if (Owner is SkillTreePartsBace skill)
            {
                target = true;
                value = skill.GetSkillData() == null ? 0 : plugin.TargetMarker(GameRecipes.SkillKey(skill.GetTreeId()));
                if (skill.m_SkillIcon != null) bounds = skill.m_SkillIcon.rectTransform;
            }
            if (value == 0 || bounds == null) { Hide(); return; }
            anchor = bounds;
            if (badge == null) CreateBadge();
            if (badge.parent != anchor) badge.SetParent(anchor, false);
            badge.SetAsLastSibling();
            glyph.Shape = target ? WishlistVectorGlyph.Glyph.Star
                : value == 2 ? WishlistVectorGlyph.Glyph.Check : WishlistVectorGlyph.Glyph.Diamond;
            glyph.color = value == 2 ? NativeWishlistUI.StockedColor : NativeWishlistUI.ShortColor;
            badge.gameObject.SetActive(true);
            marker = value;
        }

        private void CreateBadge()
        {
            var go = new GameObject("Wishlist Corner Badge", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
            badge = go.GetComponent<RectTransform>();
            badge.SetParent(anchor, false);
            badge.anchorMin = badge.anchorMax = Vector2.one;
            badge.pivot = Vector2.one;
            badge.sizeDelta = new Vector2(44, 44);
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var background = go.GetComponent<Image>();
            background.color = new Color(0.035f, 0.045f, 0.055f, 0.92f);
            background.raycastTarget = false;
            var mark = new GameObject("Wishlist Mark", typeof(RectTransform), typeof(CanvasRenderer), typeof(WishlistVectorGlyph));
            var rt = mark.GetComponent<RectTransform>();
            rt.SetParent(badge, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6, 6);
            rt.offsetMax = new Vector2(-6, -6);
            glyph = mark.GetComponent<WishlistVectorGlyph>();
            glyph.raycastTarget = false;
        }
    }

    // Vector geometry avoids reliance on missing star/check CJK-font glyphs or on a
    // private sprite atlas. MaskableGraphic respects native scrolling/masks and canvas.
    internal sealed class WishlistVectorGlyph : MaskableGraphic
    {
        internal enum Glyph { Star, Diamond, Check }
        private Glyph shape;
        internal Glyph Shape
        {
            get => shape;
            set { if (shape != value) { shape = value; SetVerticesDirty(); } }
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            Vector2 center = r.center;
            float radius = Mathf.Min(r.width, r.height) * 0.5f;
            if (shape == Glyph.Check)
            {
                Stroke(vh, center + new Vector2(-0.8f, 0) * radius, center + new Vector2(-0.25f, -0.55f) * radius, radius * 0.28f);
                Stroke(vh, center + new Vector2(-0.25f, -0.55f) * radius, center + new Vector2(0.85f, 0.7f) * radius, radius * 0.28f);
                return;
            }
            int count = shape == Glyph.Star ? 10 : 4;
            vh.AddVert(center, color, Vector2.zero);
            for (int i = 0; i < count; ++i)
            {
                float a = Mathf.PI * 0.5f + i * Mathf.PI * 2 / count;
                float scale = shape == Glyph.Star && (i & 1) != 0 ? 0.43f : 1f;
                vh.AddVert(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius * scale, color, Vector2.zero);
            }
            for (int i = 0; i < count; ++i) vh.AddTriangle(0, i + 1, (i + 1) % count + 1);
        }
        private void Stroke(VertexHelper vh, Vector2 start, Vector2 end, float width)
        {
            Vector2 direction = (end - start).normalized;
            Vector2 normal = new Vector2(-direction.y, direction.x) * width * 0.5f;
            int first = vh.currentVertCount;
            vh.AddVert(start - normal, color, Vector2.zero);
            vh.AddVert(start + normal, color, Vector2.zero);
            vh.AddVert(end + normal, color, Vector2.zero);
            vh.AddVert(end - normal, color, Vector2.zero);
            vh.AddTriangle(first, first + 1, first + 2);
            vh.AddTriangle(first, first + 2, first + 3);
        }
    }

    internal sealed class WishlistGuideBinding : MonoBehaviour
    {
        internal MenuButtonGuide Guide;
        private TextMeshProUGUI hint;
        private ButtonGuideText[] sources;
        private float nextRefresh;
        private readonly Vector3[] corners = new Vector3[4];

        internal void Hide() { if (hint != null) hint.gameObject.SetActive(false); }
        internal void Release()
        {
            enabled = false;
            Hide();
            if (hint != null) Destroy(hint.gameObject);
            hint = null;
        }
        private void OnDisable() => Hide();
        private void OnDestroy() { Release(); NativeWishlistUI.Forget(this); }

        private void LateUpdate()
        {
            var plugin = Plugin.Self;
            if (plugin == null || !plugin.FeatureEnabled || !plugin.Ready || Guide == null || !Guide.isActiveAndEnabled)
            { Hide(); return; }
            try
            {
                // CurrentSelection now reads the top dialog directly, without scene scans.
                // Hide immediately for modals/customization, independent of LateUpdate order.
                string key = GameRecipes.CurrentSelection();
                if (key == null) { Hide(); return; }
                if (Time.unscaledTime < nextRefresh) return;
                nextRefresh = Time.unscaledTime + 0.1f;
                Refresh(plugin, key);
            }
            catch (Exception e) { Hide(); enabled = false; NativeWishlistUI.Report(e); }
        }

        private void Refresh(Plugin plugin, string key)
        {
            if (sources == null || sources.Length == 0) sources = Guide.GetComponentsInChildren<ButtonGuideText>(true);
            TextMeshProUGUI source = null;
            foreach (var button in sources)
            {
                if (button == null) continue;
                var text = NativeWishlistUI.GuideTextField.GetValue(button) as TextMeshProUGUI;
                if (text == null) continue;
                if (source == null) source = text;
                if (text.isActiveAndEnabled) { source = text; break; }
            }
            if (source == null || source.font == null) { Hide(); return; }
            if (hint == null)
            {
                var go = new GameObject("Wishlist Native Guide", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI), typeof(LayoutElement));
                go.transform.SetParent(Guide.transform, false);
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                hint = go.GetComponent<TextMeshProUGUI>();
                hint.raycastTarget = false;
                hint.richText = true;
                hint.textWrappingMode = TextWrappingModes.NoWrap;
                hint.overflowMode = TextOverflowModes.Ellipsis;
                hint.alignment = TextAlignmentOptions.MidlineRight;
                hint.margin = Vector4.zero;
            }
            // Copy only native TMP style, not ButtonGuideText/localization event owners.
            // Resampling also follows game locale font and controller sprite changes.
            if (hint.font != source.font) hint.font = source.font;
            if (hint.fontSharedMaterial != source.fontSharedMaterial) hint.fontSharedMaterial = source.fontSharedMaterial;
            if (hint.spriteAsset != source.spriteAsset) hint.spriteAsset = source.spriteAsset;
            hint.fontStyle = source.fontStyle;
            hint.color = source.color;
            hint.fontSize = source.fontSize;
            hint.enableAutoSizing = true;
            hint.fontSizeMax = source.fontSize;
            hint.fontSizeMin = Mathf.Min(source.fontSize, 16);
            // The keyboard R_STICK_PUSH mapping belongs to a different native action;
            // never label our V favorite with that mapped keyboard key.
            string table = AccessTools.Method(typeof(StringManager), "GetCurrentButtonTable").Invoke(StringManager.instance, null) as string;
            if (table == "KeyboardMouse") table = "Xbox";
            string icon = StringManager.instance.GetButtonFont("R_STICK_PUSH", table: table);
            if (string.IsNullOrEmpty(icon) || icon.Contains("NotFound") || icon == "<size=100%></size>") icon = "R3";
            bool selected = plugin.IsSelected(key);
            hint.text = "V / " + icon + (selected ? " 取消收藏" : " 加入心愿单");
            hint.gameObject.SetActive(true);
            PositionHint();
        }

        private void PositionHint()
        {
            var guideRect = Guide.transform as RectTransform;
            if (guideRect == null) return;
            float left = guideRect.rect.xMin, right = guideRect.rect.xMax;
            float top = guideRect.rect.yMax, bottom = guideRect.rect.yMin;
            float usedRight = left;
            bool hasNative = false;
            foreach (var button in sources)
            {
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                var text = NativeWishlistUI.GuideTextField.GetValue(button) as TextMeshProUGUI;
                if (text == null || !text.isActiveAndEnabled) continue;
                text.rectTransform.GetWorldCorners(corners);
                foreach (var corner in corners)
                {
                    Vector3 point = guideRect.InverseTransformPoint(corner);
                    usedRight = Mathf.Max(usedRight, point.x);
                    top = Mathf.Max(top, point.y);
                    bottom = Mathf.Min(bottom, point.y);
                }
                hasNative = true;
            }
            float height = Mathf.Max(28, hint.fontSize * 1.5f);
            float preferred = hint.GetPreferredValues(hint.text).x + 20;
            var rt = hint.rectTransform;
            rt.anchorMin = rt.anchorMax = guideRect.pivot;
            rt.pivot = new Vector2(1, 0);
            // Only use genuine spare horizontal space; otherwise reserve a separate
            // native footer row ABOVE the original. Existing guide positions never move.
            bool inline = hasNative && right - usedRight - 20 >= preferred;
            float width = inline ? preferred : Mathf.Max(0, right - left);
            if (width < 100)
            {
                // A content-sized guide may be narrow: borrow available ancestor width,
                // but stay in its canvas rather than constructing a fullscreen overlay.
                var parent = guideRect.parent as RectTransform;
                if (parent != null)
                {
                    parent.GetWorldCorners(corners);
                    left = guideRect.InverseTransformPoint(corners[0]).x + 24;
                    right = guideRect.InverseTransformPoint(corners[2]).x - 24;
                    width = Mathf.Max(0, right - left);
                }
            }
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(right, inline ? bottom : top + 5);
            rt.SetAsLastSibling();
        }
    }
}
