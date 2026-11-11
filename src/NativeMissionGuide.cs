using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnomalithWishlist
{
    // An extra item in PauseMenu's ORIGINAL footer, not in the Mission detail.
    // Never register it in MenuButtonGuide.m_Texts: its deferred localization
    // coroutine must remain free to update/hide all of the original guide items.
    internal static class NativeMissionGuide
    {
        private static readonly FieldInfo GuideField = AccessTools.Field(typeof(PauseMenu), "m_MenuButtonGuide");
        private static readonly HashSet<MissionGuideBinding> Bindings = new HashSet<MissionGuideBinding>();
        private static bool stopped, reported;

        internal static void Show(QuestUI ui)
        {
            if (stopped || ui == null) return;
            try
            {
                var pause = PauseMenu.Instance;
                if (pause == null) return;
                var guide = GuideField.GetValue(pause) as MenuButtonGuide;
                if (guide == null) return;
                var binding = guide.GetComponent<MissionGuideBinding>();
                if (binding == null) binding = guide.gameObject.AddComponent<MissionGuideBinding>();
                Bindings.Add(binding);
                binding.Pause = pause;
                binding.Guide = guide;
                binding.UI = ui;
                binding.Refresh();
            }
            catch (Exception error) { Hide(ui); Report(error); }
        }

        internal static void Hide(QuestUI ui)
        {
            foreach (var binding in Bindings)
                if (binding != null && ReferenceEquals(binding.UI, ui))
                {
                    binding.UI = null;
                    binding.Hide();
                }
        }

        internal static void Shutdown()
        {
            stopped = true;
            foreach (var binding in new List<MissionGuideBinding>(Bindings))
                if (binding != null)
                {
                    binding.Release();
                    UnityEngine.Object.Destroy(binding);
                }
            Bindings.Clear();
        }

        internal static void Forget(MissionGuideBinding binding) => Bindings.Remove(binding);
        internal static void Report(Exception error)
        {
            if (reported) return;
            reported = true;
            Debug.LogWarning("[ANOMALITH Wishlist] Native Mission footer: " + error);
        }
    }

    internal sealed class MissionGuideBinding : MonoBehaviour
    {
        private const string Caption = "V / R3 取消收藏";
        private static readonly FieldInfo TextsField = AccessTools.Field(typeof(MenuButtonGuide), "m_Texts");
        private static readonly FieldInfo TextField = AccessTools.Field(typeof(ButtonGuideText), "m_Text");
        private static readonly FieldInfo DataField = AccessTools.Field(typeof(ButtonGuideText), "m_ButtonGuideData");
        private static readonly FieldInfo StartField = AccessTools.Field(typeof(ButtonGuideText), "m_StartSetting");
        internal PauseMenu Pause;
        internal MenuButtonGuide Guide;
        internal QuestUI UI;
        private ButtonGuideText clone;
        private TextMeshProUGUI hint, source;
        private TMP_FontAsset sourceFont;
        private Material sourceMaterial;
        private readonly Vector3[] corners = new Vector3[4];

        internal void Hide()
        {
            if (clone == null || !clone.gameObject.activeSelf) return;
            clone.gameObject.SetActive(false);
            Rebuild();
        }

        internal void Release()
        {
            enabled = false;
            UI = null;
            Hide();
            if (clone != null) Destroy(clone.gameObject);
            clone = null;
            hint = source = null;
        }

        private void OnDisable() => Hide();
        private void OnDestroy() { Release(); NativeMissionGuide.Forget(this); }
        private void LateUpdate() => Refresh();

        internal void Refresh()
        {
            try
            {
                var plugin = Plugin.Self;
                if (!enabled || UI == null || !UI.isActiveAndEnabled || UI.IsDisableOperation ||
                    Guide == null || !Guide.isActiveAndEnabled || Pause == null ||
                    !PauseMenu.IsOpen || Pause.IsCloseProgress || plugin == null ||
                    !plugin.FeatureEnabled || !plugin.Ready ||
                    !DialogManager.IsParentDialogTop(UI.transform))
                { Hide(); return; }
                if (clone == null) CreateHint();
                if (clone == null || source == null) { Hide(); return; }
                bool changed = !clone.gameObject.activeSelf;
                if (sourceFont != source.font || sourceMaterial != source.fontSharedMaterial || hint.text != Caption)
                {
                    sourceFont = source.font;
                    sourceMaterial = source.fontSharedMaterial;
                    hint.font = sourceFont;
                    hint.fontSharedMaterial = sourceMaterial;
                    NativeWishlistFonts.Apply(hint, Caption);
                    changed = true;
                }
                hint.spriteAsset = source.spriteAsset;
                hint.fontSize = source.fontSize;
                hint.fontStyle = source.fontStyle;
                hint.color = source.color;
                clone.gameObject.SetActive(true);
                if (changed) Rebuild();
                PositionWithoutLayout();
            }
            catch (Exception error)
            {
                Hide();
                enabled = false;
                NativeMissionGuide.Report(error);
            }
        }

        private void CreateHint()
        {
            var texts = TextsField.GetValue(Guide) as List<ButtonGuideText>;
            if (texts == null) return;
            ButtonGuideText template = null;
            foreach (var item in texts)
            {
                if (item == null || item.transform.parent == null || !item.transform.IsChildOf(Guide.transform)) continue;
                var text = TextField.GetValue(item) as TextMeshProUGUI;
                // ButtonGuideText.OnDestroy unconditionally accesses this localizer.
                // Clone the real native object, including its serialized references.
                if (text == null || !text.transform.IsChildOf(item.transform) || text.GetComponent("nnLocalizeDataUGUI") == null) continue;
                template = item;
                source = text;
                if (item.gameObject.activeInHierarchy) break;
            }
            if (template == null) return;
            clone = Instantiate(template, template.transform.parent, false);
            clone.name = "Wishlist Mission Native Footer";
            clone.gameObject.SetActive(false);
            hint = TextField.GetValue(clone) as TextMeshProUGUI;
            // Retain native components for safe destruction, but give only this
            // owned clone literal text. No native guide/localizer is modified.
            DataField.SetValue(clone, null);
            StartField.SetValue(clone, false);
            clone.enabled = false;
            var localizer = hint.GetComponent("nnLocalizeDataUGUI");
            AccessTools.Field(localizer.GetType(), "EnableStringTabel").SetValue(localizer, false);
            if (localizer is Behaviour behaviour) behaviour.enabled = false;
            hint.raycastTarget = false;
            hint.text = ""; // Force font coverage and caption setup on the first refresh.
            clone.transform.SetAsLastSibling();
        }

        private void Rebuild()
        {
            if (clone != null && clone.transform.parent is RectTransform rect)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }

        private void PositionWithoutLayout()
        {
            var parent = clone.transform.parent as RectTransform;
            if (parent == null) return;
            var layout = parent.GetComponent<LayoutGroup>();
            var element = clone.GetComponent<LayoutElement>();
            if (layout != null && layout.isActiveAndEnabled && (element == null || !element.ignoreLayout)) return;
            // Some native footer variants position their items directly. Keep the
            // native clone in that SAME footer and reserve a row above its hints,
            // rather than overlap/move the original return and navigation items.
            float left = float.PositiveInfinity, right = float.NegativeInfinity, top = float.NegativeInfinity;
            var texts = TextsField.GetValue(Guide) as List<ButtonGuideText>;
            foreach (var item in texts)
            {
                if (item == null || !item.gameObject.activeInHierarchy) continue;
                var rect = item.transform as RectTransform;
                if (rect == null) continue;
                rect.GetWorldCorners(corners);
                foreach (var corner in corners)
                {
                    var point = parent.InverseTransformPoint(corner);
                    left = Mathf.Min(left, point.x);
                    right = Mathf.Max(right, point.x);
                    top = Mathf.Max(top, point.y);
                }
            }
            if (float.IsInfinity(top)) return;
            var target = clone.transform as RectTransform;
            if (target == null) return;
            target.anchorMin = target.anchorMax = parent.pivot;
            target.pivot = new Vector2(1, 0);
            target.sizeDelta = new Vector2(Mathf.Max(right - left, hint.GetPreferredValues(Caption).x + 12),
                Mathf.Max(28, hint.fontSize * 1.5f));
            target.anchoredPosition = new Vector2(right, top + 5);
        }
    }
}
