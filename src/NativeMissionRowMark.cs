using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AnomalithWishlist
{
    // Left-list badges use the ORIGINAL quest-icon slot, not a gutter inside its text.
    // The source icon stays hidden; only the wishlist-owned vector graphic is drawn.
    internal sealed class NativeMissionRowMark : MonoBehaviour
    {
        private RectTransform source;
        private WishlistVectorGlyph glyph;
        private int state;
        private readonly Vector3[] corners = new Vector3[4];

        internal static void Bind(QuestElement row, int state)
        {
            var binding = row.GetComponent<NativeMissionRowMark>();
            if (binding == null) binding = row.gameObject.AddComponent<NativeMissionRowMark>();
            if (binding.source == null)
            {
                var icon = AccessTools.Field(typeof(QuestElement), "m_SubQuestIcon").GetValue(row) as GameObject;
                if (icon != null)
                {
                    var image = icon.GetComponentInChildren<Image>(true);
                    binding.source = image != null ? image.rectTransform : icon.transform as RectTransform;
                }
            }
            binding.state = state;
        }
        private void OnDisable()
        {
            if (glyph != null) glyph.gameObject.SetActive(false);
        }
        private void LateUpdate()
        {
            if (source == null || state == 0 || Plugin.Self == null || !Plugin.Self.FeatureEnabled)
            { OnDisable(); return; }
            if (glyph == null)
            {
                var go = new GameObject("Wishlist Native Quest Icon", typeof(RectTransform), typeof(CanvasRenderer),
                    typeof(WishlistVectorGlyph), typeof(LayoutElement));
                go.transform.SetParent(transform, false);
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                glyph = go.GetComponent<WishlistVectorGlyph>();
                glyph.raycastTarget = false;
            }
            source.GetWorldCorners(corners);
            for (int i = 0; i < corners.Length; i++) corners[i] = transform.InverseTransformPoint(corners[i]);
            var rect = glyph.rectTransform;
            rect.anchorMin = rect.anchorMax = ((RectTransform)transform).pivot;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition3D = (corners[0] + corners[2]) * 0.5f;
            rect.sizeDelta = new Vector2(Vector3.Distance(corners[0], corners[3]), Vector3.Distance(corners[0], corners[1]));
            rect.localRotation = Quaternion.Inverse(transform.rotation) * source.rotation;
            glyph.Shape = state == 2 ? WishlistVectorGlyph.Glyph.Check : WishlistVectorGlyph.Glyph.Diamond;
            glyph.color = state == 2 ? NativeWishlistUI.StockedColor : NativeWishlistUI.ShortColor;
            glyph.gameObject.SetActive(true);
        }
    }
}
