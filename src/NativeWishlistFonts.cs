using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace AnomalithWishlist
{
    // Used only on wishlist-owned text or on native text whose state the caller restores.
    // Never add fallbacks to TMP_Settings or mutate a shared game's fallback list.
    internal static class NativeWishlistFonts
    {
        private static readonly Dictionary<TMP_FontAsset, TMP_FontAsset> Proxies = new Dictionary<TMP_FontAsset, TMP_FontAsset>();
        private static readonly Dictionary<TMP_FontAsset, TMP_FontAsset> Sources = new Dictionary<TMP_FontAsset, TMP_FontAsset>();
        private static readonly Dictionary<Font, TMP_FontAsset> DynamicFonts = new Dictionary<Font, TMP_FontAsset>();
        private static readonly HashSet<TMP_FontAsset> Owned = new HashSet<TMP_FontAsset>();
        private static TMP_FontAsset[] loaded;
        private static float nextDiscovery;
        private static bool reported;
        private static readonly Regex Tags = new Regex("<[^>]+>");

        internal static void Apply(TMP_Text text, string value)
        {
            if (text == null) return;
            value = value ?? "";
            text.text = value;
            value = new string((text.richText ? Tags.Replace(value, "") : value)
                .Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                var original = text.font;
                if (original == null) return;
                if (Covers(original, value, true))
                {
                    if (text.fontSharedMaterial == null || text.fontSharedMaterial.mainTexture != original.atlasTexture)
                        text.fontSharedMaterial = original.material;
                    return;
                }
                if (Sources.TryGetValue(original, out var source)) original = source;
                if (loaded == null || Time.unscaledTime >= nextDiscovery)
                {
                    nextDiscovery = Time.unscaledTime + 5;
                    loaded = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                        .Where(f => f != null && !Owned.Contains(f)).ToArray();
                }
                // Localized quest UI fonts are already loaded when this is called. Prefer
                // a complete native font over a stale generic prefab's partial atlas.
                foreach (var candidate in loaded)
                {
                    if (candidate == null || candidate == original || !Covers(candidate, value, true)) continue;
                    SetFont(text, candidate);
                    return;
                }
                // Static atlases can each contain only part of a generated sentence.
                // Compose them on a private read-only proxy, NOT on the native font asset.
                if (!Proxies.TryGetValue(original, out var proxy) || proxy == null)
                {
                    proxy = UnityEngine.Object.Instantiate(original);
                    proxy.name = original.name + " (Wishlist local fallback)";
                    proxy.hideFlags = HideFlags.HideAndDontSave;
                    proxy.atlasPopulationMode = AtlasPopulationMode.Static;
                    Owned.Add(proxy); Proxies[original] = proxy; Sources[proxy] = original;
                }
                var fallbacks = new List<TMP_FontAsset>();
                if (original.fallbackFontAssetTable != null)
                    fallbacks.AddRange(original.fallbackFontAssetTable.Where(f => f != null && !Owned.Contains(f)));
                fallbacks.AddRange(loaded.Where(f => f != null && f != original));
                proxy.fallbackFontAssetTable = fallbacks.Distinct().ToList();
                if (!Covers(proxy, value, true))
                {
                    // If the game retained its embedded source font, construct our own
                    // dynamic atlas for glyphs absent from its static localization atlases.
                    foreach (var font in loaded.Where(f => f != null && f.sourceFontFile != null)
                        .Select(f => f.sourceFontFile).Distinct())
                    {
                        if (!DynamicFonts.TryGetValue(font, out var dynamicFont) || dynamicFont == null)
                        {
                            dynamicFont = TMP_FontAsset.CreateFontAsset(font);
                            if (dynamicFont == null) continue;
                            dynamicFont.name = "Wishlist glyphs - " + font.name;
                            dynamicFont.hideFlags = HideFlags.HideAndDontSave;
                            dynamicFont.isMultiAtlasTexturesEnabled = true;
                            DynamicFonts[font] = dynamicFont; Owned.Add(dynamicFont);
                        }
                        proxy.fallbackFontAssetTable.Add(dynamicFont);
                        if (Covers(proxy, value, true)) break;
                    }
                }
                SetFont(text, proxy);
                if (!Covers(proxy, value, true) && !reported)
                {
                    reported = true;
                    Debug.LogWarning("[AnomalithWishlist] Some wishlist glyphs are not available in the currently loaded native fonts.");
                }
            }
            catch (Exception e)
            {
                if (!reported) { reported = true; Debug.LogWarning("[AnomalithWishlist] Native font fallback unavailable: " + e.Message); }
            }
        }
        private static bool Covers(TMP_FontAsset font, string value, bool add)
            => font != null && font.HasCharacters(value, out uint[] missing, searchFallbacks: true, tryAddCharacter: add);
        private static void SetFont(TMP_Text text, TMP_FontAsset font)
        {
            text.font = font;
            // Keep atlas texture/material paired; reusing the old font's material is
            // another cause of missing squares even when the new font has the glyphs.
            text.fontSharedMaterial = font.material;
        }
        internal static void Shutdown()
        {
            // Caller first restores borrowed native text / removes owned UI.
            foreach (var font in DynamicFonts.Values)
            {
                if (font == null) continue;
                foreach (var texture in font.atlasTextures) if (texture != null) UnityEngine.Object.Destroy(texture);
                if (font.material != null) UnityEngine.Object.Destroy(font.material);
            }
            foreach (var font in Owned) if (font != null) UnityEngine.Object.Destroy(font);
            Owned.Clear(); Proxies.Clear(); Sources.Clear(); DynamicFonts.Clear(); loaded = null; nextDiscovery = 0;
        }
    }
}
