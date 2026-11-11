using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AnomalithWishlist
{
    // Vector badges in a reserved leading-space gutter, not font characters.
    // TMP character positions keep each badge attached to its material when text wraps,
    // the native detail scrolls, or the Canvas/font size changes.
    internal sealed class NativeMissionMaterialMarks : MonoBehaviour
    {
        internal struct Mark
        {
            internal int Index, State;
            internal Mark(int index, int state) { Index = index; State = state; }
        }
        internal const string Gutter = "      ";
        private TMP_Text text;
        private readonly List<Mark> marks = new List<Mark>();
        private readonly List<WishlistVectorGlyph> glyphs = new List<WishlistVectorGlyph>();

        internal static void Bind(TMP_Text target, List<Mark> values)
        {
            if (target == null) return;
            var binding = target.GetComponent<NativeMissionMaterialMarks>();
            if (binding == null) binding = target.gameObject.AddComponent<NativeMissionMaterialMarks>();
            binding.text = target;
            binding.marks.Clear(); binding.marks.AddRange(values);
            binding.enabled = true;
        }
        internal static void Hide(TMP_Text target)
        {
            if (target == null) return;
            var binding = target.GetComponent<NativeMissionMaterialMarks>();
            if (binding == null) return;
            binding.marks.Clear(); binding.HideGlyphs();
        }
        internal static void Shutdown()
        {
            foreach (var binding in Resources.FindObjectsOfTypeAll<NativeMissionMaterialMarks>())
            {
                if (binding == null) continue;
                binding.enabled = false;
                foreach (var glyph in binding.glyphs) if (glyph != null) Destroy(glyph.gameObject);
                binding.glyphs.Clear(); Destroy(binding);
            }
        }
        private void HideGlyphs()
        {
            foreach (var glyph in glyphs) if (glyph != null) glyph.gameObject.SetActive(false);
        }
        private void OnDisable() => HideGlyphs();
        private void LateUpdate()
        {
            if (marks.Count == 0 || text == null || !text.isActiveAndEnabled ||
                Plugin.Self == null || !Plugin.Self.FeatureEnabled) { HideGlyphs(); return; }
            text.ForceMeshUpdate();
            var info = text.textInfo;
            int used = 0;
            foreach (var mark in marks)
            {
                if (mark.State == 0) continue; // Unknown stock must not imply shortage/readiness.
                int first = -1, last = -1;
                for (int i = 0; i < info.characterCount; i++)
                {
                    if (info.characterInfo[i].index == mark.Index) first = i;
                    if (info.characterInfo[i].index == mark.Index + Gutter.Length - 1) { last = i; break; }
                }
                if (first < 0 || last < first) continue;
                var start = info.characterInfo[first];
                var end = info.characterInfo[last];
                if (start.lineNumber != end.lineNumber) continue;
                float width = end.xAdvance - start.origin;
                float height = start.ascender - start.descender;
                float size = Mathf.Min(44, Mathf.Min(width - 4, height * 0.85f));
                if (size < 8) continue;
                if (used == glyphs.Count)
                {
                    var go = new GameObject("Wishlist Material State", typeof(RectTransform), typeof(CanvasRenderer), typeof(WishlistVectorGlyph));
                    go.transform.SetParent(text.transform, false);
                    var glyph = go.GetComponent<WishlistVectorGlyph>();
                    glyph.raycastTarget = false;
                    glyphs.Add(glyph);
                }
                var icon = glyphs[used++];
                icon.Shape = mark.State == 2 ? WishlistVectorGlyph.Glyph.Check : WishlistVectorGlyph.Glyph.Diamond;
                icon.color = mark.State == 2 ? NativeWishlistUI.StockedColor : NativeWishlistUI.ShortColor;
                var rect = icon.rectTransform;
                rect.anchorMin = rect.anchorMax = text.rectTransform.pivot;
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(size, size);
                rect.anchoredPosition = new Vector2(start.origin + width * 0.5f, (start.ascender + start.descender) * 0.5f);
                icon.gameObject.SetActive(true);
            }
            for (int i = used; i < glyphs.Count; i++) glyphs[i].gameObject.SetActive(false);
        }
    }
}
