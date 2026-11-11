using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnomalithWishlist
{
    // Parameters and the extra category belong to one OptionMenu, NEVER to
    // OptionParams.m_AllParams / its ScriptableObject (which SaveDataOption serializes).
    internal static class NativeSettings
    {
        private static readonly FieldInfo OldValues = AccessTools.Field(typeof(OptionMenu), "m_OldOptionParams");
        private static readonly FieldInfo HolderPrefab = AccessTools.Field(typeof(OptionMenu), "m_HolderPrefab");
        private static readonly FieldInfo HolderRoot = AccessTools.Field(typeof(OptionMenu), "m_HolderRoot");
        private static readonly FieldInfo OptionRoot = AccessTools.Field(typeof(OptionMenu), "m_OptionItemRoot");
        private static readonly FieldInfo StartupScene = AccessTools.Field(typeof(OptionMenu), "startupScene");
        private static readonly FieldInfo CurrentHolder = AccessTools.Field(typeof(OptionMenu), "m_CurrentSelectHolder");
        private static readonly FieldInfo SelectHolderNum = AccessTools.Field(typeof(OptionMenu), "m_SelectHolderNum");
        private static readonly MethodInfo SelectHolder = AccessTools.Method(typeof(OptionMenu), "SetSelectHolderNum");
        private static readonly MethodInfo CloseHolder = AccessTools.Method(typeof(OptionMenu), "CloseHolder");
        private static readonly FieldInfo HolderName = AccessTools.Field(typeof(OptionItemHolder), "m_HolderName");
        private static readonly FieldInfo HolderOpen = AccessTools.Field(typeof(OptionItemHolder), "m_IsOpen");
        private static readonly FieldInfo ItemParam = AccessTools.Field(typeof(OptionItemBase), "m_OptionParam");
        private static readonly FieldInfo ItemName = AccessTools.Field(typeof(OptionItemBase), "m_ItemNameText");
        private static readonly FieldInfo SelectText = AccessTools.Field(typeof(OptionItemSelect), "m_SelectText");
        private static readonly FieldInfo DialogCaption = AccessTools.Field(typeof(DialogGeneral), "m_Caption");
        private static readonly FieldInfo DialogMessage = AccessTools.Field(typeof(DialogGeneral), "m_Message");
        private static readonly Dictionary<OptionMenu, MenuState> Menus = new Dictionary<OptionMenu, MenuState>();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Language GameLanguage = new Language();
        private static readonly OptionGroupType ModGroup = FindModGroup();
        private static long nextId;
        [ThreadStatic] private static MenuState building;

        private sealed class Entry
        {
            internal MenuState Owner;
            internal OptionParam Param;
            internal bool IsEnabled;
            internal int InitialValue;
            internal readonly List<OptionItemBase> Rows = new List<OptionItemBase>();
        }

        private sealed class MenuState
        {
            internal OptionMenu Menu;
            internal OptionItemHolder Holder;
            internal Entry Enabled, Mode;
            internal readonly List<OptionParam> Parameters = new List<OptionParam>();
            internal bool Committed, Removed, EnableApproved, RiskPending;
            internal DialogGeneral RiskDialog;
            internal Action RiskClosed;
            internal int SuppressInputUntilFrame;
        }

        private static OptionGroupType FindModGroup()
        {
            // Native values are 0..9, not declaration-order indices. Never reuse NONE,
            // a native group, or change the enum iteration used by other menus.
            int value = int.MaxValue;
            while (Enum.IsDefined(typeof(OptionGroupType), value)) --value;
            return (OptionGroupType)value;
        }

        private static Entry NewEntry(MenuState state, string id, bool isEnabled, int value)
        {
            var entry = new Entry
            {
                Owner = state,
                IsEnabled = isEnabled,
                InitialValue = value,
                Param = new OptionParam
                {
                    m_Id = id,
                    m_Value = value,
                    // Native ON_OFF is 0 = on, 1 = off; both reset to OFF.
                    m_DefaultValue = 1,
                    m_MinValue = 0,
                    m_MaxValue = 1,
                    m_OptionParamType = OptionParamType.SELECT,
                    m_OptionGroupType = ModGroup,
                    m_OptionSelectType = OptionSelectType.ON_OFF,
                    m_ParentId = "",
                    m_ParamLock = false
                }
            };
            Entries.Add(id, entry);
            state.Parameters.Add(entry.Param);
            return entry;
        }

        private static void Begin(OptionMenu menu)
        {
            if (Plugin.Self == null) return;
            Remove(menu, true);
            string prefix = "ANOMALITH_WISHLIST_V12_" + (++nextId) + "_";
            var state = new MenuState { Menu = menu };
            state.Enabled = NewEntry(state, prefix + "ENABLED", true, Plugin.Self.FeatureEnabled ? 0 : 1);
            state.Mode = NewEntry(state, prefix + "GLOBAL", false, Plugin.Self.AllMode ? 0 : 1);
            Menus.Add(menu, state);
            var lifetime = menu.GetComponent<WishlistSettingsLifetime>();
            if (lifetime == null) lifetime = menu.gameObject.AddComponent<WishlistSettingsLifetime>();
            lifetime.Menu = menu;
        }

        private static bool Owned(OptionParam param, out Entry entry)
        {
            entry = null;
            return param != null && param.m_Id != null && Entries.TryGetValue(param.m_Id, out entry)
                && ReferenceEquals(entry.Param, param);
        }

        private static bool Alive(MenuState state)
        {
            return !state.Removed && state.Menu != null && Menus.TryGetValue(state.Menu, out var current)
                && ReferenceEquals(current, state);
        }

        private static void AppendHolder(OptionMenu menu)
        {
            if (!Menus.TryGetValue(menu, out var state) || state.Holder != null) return;
            var root = (RectTransform)HolderRoot.GetValue(menu);
            // Match OptionMenu.CreateHolder's native wiring, including the actual holder
            // list used for pad wrapping, mouse selection, open/back and button guides.
            var holder = UnityEngine.Object.Instantiate((OptionItemHolder)HolderPrefab.GetValue(menu), root);
            state.Holder = holder; // Own even a partially built clone so failure can clean up.
            holder.StartupScene = (OptionMenu.StartupScene)StartupScene.GetValue(menu);
            holder.SetOptionMenu(menu);
            LayoutRebuilder.ForceRebuildLayoutImmediate(root);
            holder.SetOptionRootPos(((RectTransform)OptionRoot.GetValue(menu)).position);
            holder.CreateOptionList(ModGroup);
            holder.UnUsedSelect();
            var holders = menu.GetOptionHolderList();
            holder.ObjectNumber = holders.Count;
            holder.OnMouseEnterAction = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), menu, SelectHolder);
            holder.IsInputActive = true;
            holders.Add(holder);
            LayoutRebuilder.ForceRebuildLayoutImmediate(root);
            RefreshHeader(state);
        }

        // nnLocalizeDataUGUI's public _EnableStringTabel setter is a no-op in this
        // build. Keep its locale-specific font/material updates, disable only lookup.
        private static void UseLiteralText(TextMeshProUGUI text)
        {
            if (text == null) return;
            Component localizer = text.GetComponent("nnLocalizeDataUGUI");
            if (localizer != null)
                AccessTools.Field(localizer.GetType(), "EnableStringTabel")?.SetValue(localizer, false);
        }

        internal static int CurrentLanguage => GameLanguage.Get();

        private static string Localized(int language, string ja, string en, string zh, string tw, string ko)
        {
            // Language.Get() returns SysLanguage's order, not the LANGUAGE option index.
            switch (language)
            {
                case 0: return zh;
                case 1: return tw;
                case 3: return ja;
                case 4: return ko;
                default: return en;
            }
        }

        private static void RefreshHeader(MenuState state)
        {
            if (state.Holder == null) return;
            var text = (TextMeshProUGUI)HolderName.GetValue(state.Holder);
            UseLiteralText(text);
            if (text != null) text.text = Localized(CurrentLanguage, "Mod設定", "Mod settings", "Mod配置", "Mod配置", "Mod 설정");
        }

        private static void RefreshRow(OptionItemBase row, Entry entry)
        {
            if (row == null) return;
            int language = CurrentLanguage;
            var name = (TextMeshProUGUI)ItemName.GetValue(row);
            if (name != null)
                name.text = entry.IsEnabled
                    ? Localized(language, "素材ウィッシュリスト", "Material wishlist", "材料心愿单", "材料願望清單", "재료 위시리스트")
                    : Localized(language, "全素材の通知", "Global material reminders", "全局材料提醒", "全域材料提醒", "전체 재료 알림");
            if (!(row is OptionItemSelect select)) return;
            var value = (TextMeshProUGUI)SelectText.GetValue(select);
            if (value == null) return;
            string native = StringManager.instance != null
                ? StringManager.instance.GetString("Option", "ON_OFF_" + entry.Param.m_Value) : null;
            value.text = !string.IsNullOrEmpty(native) ? native
                : entry.Param.m_Value == 0
                    ? Localized(language, "オン", "On", "开启", "開啟", "켜기")
                    : Localized(language, "オフ", "Off", "关闭", "關閉", "끄기");
        }

        private static void UpdateRows(Entry entry)
        {
            foreach (var row in entry.Rows) if (row != null) row.UIUpdate();
        }

        internal static void RefreshLabels(OptionMenu menu)
        {
            if (menu == null || !Menus.TryGetValue(menu, out var state)) return;
            RefreshHeader(state);
            foreach (var row in state.Enabled.Rows) RefreshRow(row, state.Enabled);
            foreach (var row in state.Mode.Rows) RefreshRow(row, state.Mode);
        }

        private static void FinishRisk(MenuState state)
        {
            if (!Alive(state)) return;
            state.RiskDialog = null;
            state.RiskClosed = null;
            state.RiskPending = false;
            state.SuppressInputUntilFrame = Time.frameCount + 1;
            if (state.Holder != null) state.Holder.SetInputLock(false);
            UpdateRows(state.Enabled);
        }

        internal static void Maintain(OptionMenu menu)
        {
            if (menu == null || !Menus.TryGetValue(menu, out var state)) return;
            // Scene/UI destruction can bypass DialogBase.Destroy and its OnClose event.
            if (state.RiskPending && state.RiskDialog == null) FinishRisk(state);
        }

        private static void RequestEnable(MenuState state)
        {
            // Refuse by default, including creation failures, cancel/back and unload.
            state.Enabled.Param.m_Value = 1;
            state.EnableApproved = false;
            UpdateRows(state.Enabled);
            if (!Alive(state) || state.Committed || state.RiskPending || !state.Menu.IsTopDialog) return;
            state.RiskPending = true;
            if (state.Holder != null) state.Holder.SetInputLock(true);
            try
            {
                var dialog = DialogManager.CreateDialog<DialogGeneral>(DialogType.General);
                state.RiskDialog = dialog;
                int language = CurrentLanguage;
                string caption = Localized(language, "Modを有効にしますか？", "Enable material wishlist?", "启用材料心愿单？", "啟用材料願望清單？", "재료 위시리스트를 켤까요?");
                string message = Localized(language,
                    "ゲーム内UIでR3 / Vを押してお気に入りを登録し、必要素材を表示します。全素材の通知は追加の機能です。\n非公式Modです。先にセーブをバックアップしてください。売却の安全性は保証しません。\n有効にしますか？（設定の適用後に有効）",
                    "Mark favorites with R3 / V in the native UI and see material reminders. Global reminders are an optional addition.\nUnofficial mod: back up saves first. No guarantee that an item is safe to sell.\nEnable? (Takes effect after applying settings.)",
                    "在原生界面用 R3 / V 标记心愿单，查看材料提醒；全局材料提醒可额外开启。\n非官方 Mod，请先备份存档。不保证任何材料可安全出售。\n是否启用？（应用设置后生效）",
                    "在原生介面用 R3 / V 標記願望清單，查看材料提醒；全域材料提醒可額外開啟。\n非官方 Mod，請先備份存檔。不保證任何材料可安全出售。\n是否啟用？（套用設定後生效）",
                    "게임 UI에서 R3 / V로 즐겨찾기를 표시하고 재료 알림을 확인합니다. 전체 재료 알림은 추가 기능입니다.\n비공식 Mod입니다. 먼저 저장 파일을 백업하세요. 안전한 판매를 보장하지 않습니다.\n켤까요? (설정 적용 후 활성화)");
                UseLiteralText((TextMeshProUGUI)DialogCaption.GetValue(dialog));
                UseLiteralText((TextMeshProUGUI)DialogMessage.GetValue(dialog));
                dialog.SetDialog(DialogGeneral.DialogType.YesNo, caption, message, button =>
                {
                    if (!Alive(state) || state.Committed || !ReferenceEquals(state.RiskDialog, dialog)) return;
                    state.EnableApproved = button == DialogGeneral.ButtonType.YES;
                    state.Enabled.Param.m_Value = state.EnableApproved ? 0 : 1;
                    UpdateRows(state.Enabled);
                }, () =>
                {
                    if (!Alive(state)) return;
                    state.EnableApproved = false;
                    state.Enabled.Param.m_Value = 1;
                    UpdateRows(state.Enabled);
                });
                state.RiskClosed = () => FinishRisk(state);
                dialog.Open(state.RiskClosed);
                dialog.SetButtonCursor(DialogGeneral.ButtonType.NO);
            }
            catch (Exception error)
            {
                state.Enabled.Param.m_Value = 1;
                state.EnableApproved = false;
                DismissRisk(state);
                FinishRisk(state);
                Debug.LogWarning("[AnomalithWishlist] Native enable notice unavailable; feature remains off: " + error);
            }
        }

        private static void DismissRisk(MenuState state)
        {
            var dialog = state.RiskDialog;
            state.RiskDialog = null;
            state.RiskPending = false;
            if (dialog != null)
            {
                dialog.OnSubmit = null;
                dialog.OnCancel = null;
                if (state.RiskClosed != null) dialog.OnClose -= state.RiskClosed;
                dialog.Close();
                dialog.Destroy(); // Remove from DialogManager immediately, not just the hierarchy.
            }
            state.RiskClosed = null;
        }

        private static void Commit(OptionMenu menu)
        {
            if (Plugin.Self == null || !Menus.TryGetValue(menu, out var state) || state.Committed) return;
            // NO in native Close already restored our menu-owned values through
            // SetParamValue. YES retains drafts. No config was changed at toggle time.
            if (state.Enabled.InitialValue == 1 && state.Enabled.Param.m_Value == 0 && !state.EnableApproved)
                state.Enabled.Param.m_Value = 1; // Never enable without the native consent dialog.
            if (state.Enabled.Param.m_Value != state.Enabled.InitialValue)
                Plugin.Self.SetFeatureEnabled(state.Enabled.Param.m_Value == 0);
            if (state.Mode.Param.m_Value != state.Mode.InitialValue)
                Plugin.Self.SetAllMode(state.Mode.Param.m_Value == 0);
            state.Committed = true;
        }

        private static void Remove(OptionMenu menu, bool removeHolder)
        {
            if (ReferenceEquals(menu, null) || !Menus.TryGetValue(menu, out var state)) return;
            state.Removed = true; // Invalidate all asynchronous dialog callbacks first.
            DismissRisk(state);
            if (menu != null)
            {
                var oldValues = (Dictionary<string, int>)OldValues.GetValue(menu);
                oldValues.Remove(state.Enabled.Param.m_Id);
                oldValues.Remove(state.Mode.Param.m_Id);
                var holders = menu.GetOptionHolderList();
                var holder = state.Holder;
                int index = holders.IndexOf(holder);
                bool selected = ReferenceEquals(CurrentHolder.GetValue(menu), holder) && holder != null;
                if (removeHolder && holder != null)
                {
                    if (selected && (bool)HolderOpen.GetValue(holder)) CloseHolder.Invoke(menu, null);
                    holder.SetInputLock(true);
                    holder.CloseHolder();
                    holder.IsInputActive = false;
                    holder.OnMouseEnterAction = null;
                    holder.UnUsedSelect();
                    holder.gameObject.SetActive(false);
                }
                if (index >= 0) holders.RemoveAt(index);
                for (int i = 0; i < holders.Count; ++i) holders[i].ObjectNumber = i;
                if (selected)
                {
                    int replacementIndex = holders.Count == 0 ? -1 : Mathf.Clamp(index, 0, holders.Count - 1);
                    var replacement = replacementIndex < 0 ? null : holders[replacementIndex];
                    CurrentHolder.SetValue(menu, replacement);
                    SelectHolderNum.SetValue(menu, Mathf.Max(0, replacementIndex));
                    if (removeHolder && replacement != null)
                    {
                        replacement.SetOptionRootPos(((RectTransform)OptionRoot.GetValue(menu)).position);
                        replacement.UseSelect();
                    }
                }
                if (removeHolder && holder != null)
                {
                    // The clone owns its rows/content; never reparent or delete native rows.
                    UnityEngine.Object.Destroy(holder.gameObject);
                    LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)HolderRoot.GetValue(menu));
                }
            }
            Entries.Remove(state.Enabled.Param.m_Id);
            Entries.Remove(state.Mode.Param.m_Id);
            state.Parameters.Clear();
            state.Enabled.Rows.Clear();
            state.Mode.Rows.Clear();
            Menus.Remove(menu);
        }

        internal static void Forget(OptionMenu menu) => Remove(menu, false);

        // Call before UnpatchSelf on actual unload, NOT when the feature is switched off.
        internal static void Shutdown()
        {
            foreach (var menu in new List<OptionMenu>(Menus.Keys))
            {
                Remove(menu, true);
                if (menu == null) continue;
                var lifetime = menu.GetComponent<WishlistSettingsLifetime>();
                if (lifetime == null) continue;
                lifetime.Menu = null;
                lifetime.enabled = false;
                UnityEngine.Object.Destroy(lifetime);
            }
            building = null;
        }

        [HarmonyPatch(typeof(OptionMenu), nameof(OptionMenu.Open))]
        private static class OpenPatch
        {
            private static void Prefix(OptionMenu __instance) => Begin(__instance);
            private static void Postfix(OptionMenu __instance)
            {
                if (!Menus.TryGetValue(__instance, out var state)) return;
                var oldValues = (Dictionary<string, int>)OldValues.GetValue(__instance);
                if (state.Enabled.Rows.Count > 0) oldValues[state.Enabled.Param.m_Id] = state.Enabled.InitialValue;
                if (state.Mode.Rows.Count > 0) oldValues[state.Mode.Param.m_Id] = state.Mode.InitialValue;
            }
            private static Exception Finalizer(OptionMenu __instance, Exception __exception)
            {
                if (__exception != null) Remove(__instance, true);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(OptionMenu), "CreateHolder")]
        private static class CreateHolderPatch
        {
            private static void Postfix(OptionMenu __instance) => AppendHolder(__instance);
        }

        [HarmonyPatch(typeof(OptionItemHolder), nameof(OptionItemHolder.CreateOptionList))]
        private static class BuildHolderPatch
        {
            private static void Prefix(OptionItemHolder __instance, ref OptionGroupType optionGroupType, out MenuState __state)
            {
                __state = building;
                building = null;
                var menu = __instance.GetOptionMenu();
                if (optionGroupType != ModGroup || menu == null || !Menus.TryGetValue(menu, out var state)
                    || !ReferenceEquals(state.Holder, __instance)) return;
                building = state;
                // Only this owned creation call uses SOUND as a valid temporary header
                // localization key. GroupPatch replaces its parameter query completely;
                // the finalizer restores our synthetic group. No StringManager patch.
                optionGroupType = OptionGroupType.SOUND;
            }
            private static Exception Finalizer(OptionItemHolder __instance, MenuState __state, Exception __exception)
            {
                var owned = building;
                building = __state;
                if (owned != null && ReferenceEquals(owned.Holder, __instance))
                {
                    __instance.m_HolderGroupType = ModGroup;
                    if (__exception == null) RefreshHeader(owned);
                }
                return __exception;
            }
        }

        [HarmonyPatch(typeof(OptionParams), nameof(OptionParams.GetOptionParamsInGroup))]
        private static class GroupPatch
        {
            private static bool Prefix(OptionGroupType optionGroupType, ref List<OptionParam> __result)
            {
                if (building == null || optionGroupType != OptionGroupType.SOUND) return true;
                // Native CreateOptionList supplies prefab, row indices, mouse actions,
                // pad navigation, scroll/reset and guides from this menu-owned list.
                __result = new List<OptionParam>(building.Parameters);
                return false;
            }
        }

        [HarmonyPatch(typeof(OptionParams), nameof(OptionParams.GetParamValue))]
        private static class GetValuePatch
        {
            private static bool Prefix(string id, ref int __result)
            {
                if (id == null || !Entries.TryGetValue(id, out var entry)) return true;
                __result = entry.Param.m_Value;
                return false;
            }
        }

        [HarmonyPatch(typeof(OptionParams), nameof(OptionParams.SetParamValue))]
        private static class SetValuePatch
        {
            private static bool Prefix(string id, int value)
            {
                if (id == null || !Entries.TryGetValue(id, out var entry)) return true;
                // Native discard restoration is NOT a user toggle and must not prompt.
                entry.Param.m_Value = Mathf.Clamp(value, 0, 1);
                UpdateRows(entry);
                return false;
            }
        }

        [HarmonyPatch(typeof(OptionParams), nameof(OptionParams.ResetAllPatam))]
        private static class ResetPatch
        {
            private static void Postfix(OptionGroupType groupType)
            {
                if (groupType != OptionGroupType.NONE && groupType != ModGroup) return;
                foreach (var state in Menus.Values)
                {
                    if (state.Committed) continue;
                    state.Enabled.Param.m_Value = state.Enabled.Param.m_DefaultValue;
                    state.Mode.Param.m_Value = state.Mode.Param.m_DefaultValue;
                    state.EnableApproved = false;
                    UpdateRows(state.Enabled);
                    UpdateRows(state.Mode);
                }
            }
        }

        [HarmonyPatch(typeof(OptionItemBase), nameof(OptionItemBase.SetParam))]
        private static class BindRowPatch
        {
            private static bool Prefix(OptionItemBase __instance, OptionParam optionParam)
            {
                if (!Owned(optionParam, out var entry)) return true;
                ItemParam.SetValue(__instance, optionParam);
                if (!entry.Rows.Contains(__instance)) entry.Rows.Add(__instance);
                UseLiteralText((TextMeshProUGUI)ItemName.GetValue(__instance));
                if (__instance is OptionItemSelect select)
                    UseLiteralText((TextMeshProUGUI)SelectText.GetValue(select));
                RefreshRow(__instance, entry);
                return false;
            }
        }

        [HarmonyPatch(typeof(OptionItemSelect), nameof(OptionItemSelect.UIUpdate))]
        private static class RefreshRowPatch
        {
            private static void Postfix(OptionItemSelect __instance)
            {
                if (Owned(__instance.GetOptionParam(), out var entry)) RefreshRow(__instance, entry);
            }
        }

        [HarmonyPatch]
        private static class TogglePatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(OptionItemSelect), "UseUpdate");
                yield return AccessTools.Method(typeof(OptionItemSelect), nameof(OptionItemSelect.MouseInput));
            }
            private static bool Prefix(OptionItemSelect __instance, out int __state)
            {
                __state = -1;
                if (!Owned(__instance.GetOptionParam(), out var entry)) return true;
                var state = entry.Owner;
                // Native row UseUpdate does not itself check the dialog stack or holder
                // input lock. Do not let the risk/apply dialog's pad input edit drafts.
                if (!Alive(state) || state.Committed || state.RiskPending || state.Holder == null
                    || state.Holder.IsInputLock || state.Holder.m_OpenCurrentFrame
                    || !(bool)HolderOpen.GetValue(state.Holder) || !state.Menu.IsTopDialog
                    || state.Menu.m_State != PanelBase.State.Open || Time.frameCount <= state.SuppressInputUntilFrame)
                    return false;
                __state = entry.Param.m_Value;
                return true;
            }
            private static void Postfix(OptionItemSelect __instance, int __state)
            {
                if (__state < 0 || !Owned(__instance.GetOptionParam(), out var entry) || !entry.IsEnabled) return;
                if (__state == 1 && entry.Param.m_Value == 0) RequestEnable(entry.Owner);
                else if (entry.Param.m_Value == 1) entry.Owner.EnableApproved = false;
            }
        }

        [HarmonyPatch(typeof(OptionMenu), nameof(OptionMenu.Close))]
        private static class ClosePatch
        {
            private static bool Prefix(OptionMenu __instance)
            {
                if (!Menus.TryGetValue(__instance, out var state)) return true;
                if (state.RiskPending) return false;
                // Defensive fallback for a staged enable not made through the native
                // input paths. Show consent while the menu still exists, before apply.
                if (state.Enabled.InitialValue == 1 && state.Enabled.Param.m_Value == 0 && !state.EnableApproved)
                {
                    RequestEnable(state);
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(OptionMenu), "OptionClose")]
        private static class CommitPatch
        {
            private static void Prefix(OptionMenu __instance) => Commit(__instance);
        }

        [HarmonyPatch(typeof(DialogBase), nameof(DialogBase.Destroy))]
        private static class DestroyPatch
        {
            private static void Prefix(DialogBase __instance)
            {
                if (__instance is OptionMenu menu) Forget(menu);
            }
        }
    }

    // Scene destruction may bypass DialogBase.Destroy. Refresh literals after native
    // asynchronous language changes, using the actual locale rather than draft options.
    internal sealed class WishlistSettingsLifetime : MonoBehaviour
    {
        internal OptionMenu Menu;
        private int language = -1;
        private void LateUpdate()
        {
            NativeSettings.Maintain(Menu);
            int current = NativeSettings.CurrentLanguage;
            if (current == language) return;
            language = current;
            NativeSettings.RefreshLabels(Menu);
        }
        private void OnDestroy() => NativeSettings.Forget(Menu);
    }
}
