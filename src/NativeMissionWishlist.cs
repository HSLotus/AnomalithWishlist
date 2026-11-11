using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace AnomalithWishlist
{
    // Only QuestUI's transient view dictionaries contain our IDs. Never register a
    // QuestInfo, call GetQuestProgress with them, or write them to tracking/save data.
    internal static class NativeMissionWishlist
    {
        private static readonly FieldInfo RowsField = AccessTools.Field(typeof(QuestUI), "questList");
        private static readonly FieldInfo TabsField = AccessTools.Field(typeof(QuestUI), "tabQuestList");
        private static readonly FieldInfo CursorField = AccessTools.Field(typeof(QuestUI), "cursorIdx");
        private static readonly MethodInfo SetTextMethod = AccessTools.Method(typeof(QuestUI), "SetQuestText");
        private static readonly MethodInfo ScrollMethod = AccessTools.Method(typeof(QuestUI), "ScrollElement");
        private static readonly Dictionary<QuestUI, View> Views = new Dictionary<QuestUI, View>();
        private static bool inputHookReady, shuttingDown, reported, cancelNeedsRelease;
        private static int inputBlockedThroughFrame = -1;
        private static readonly FieldInfo InstructionLabels = AccessTools.Field(typeof(CodeInstruction), "labels");

        private sealed class View
        {
            internal QuestUI UI;
            internal GameObject Section;
            internal RectTransform Content;
            internal readonly string Prefix = "\u001fWishlistUI/" + Guid.NewGuid().ToString("N") + "/";
            internal readonly Dictionary<string, string> Targets = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly List<string> Keys = new List<string>();
            internal DetailSnapshot Detail;
            internal int Revision = -1;
            internal bool Confirming, Faulted;
            internal DialogGeneral Confirmation;
            internal readonly List<TextSnapshot> ConfirmationTexts = new List<TextSnapshot>();
        }

        private static T Field<T>(object obj, string name) => (T)AccessTools.Field(obj.GetType(), name).GetValue(obj);
        private static Dictionary<string, QuestElement> Rows(QuestUI ui) => (Dictionary<string, QuestElement>)RowsField.GetValue(ui);
        private static List<string> Order(QuestUI ui)
        {
            var tabs = (Dictionary<QuestManager.QuestType, List<string>>)TabsField.GetValue(ui);
            return tabs.TryGetValue(QuestManager.QuestType.MainQuest, out var list) ? list : null;
        }
        private static string Selected(QuestUI ui)
        {
            var order = Order(ui);
            int index = (int)CursorField.GetValue(ui);
            return order != null && index >= 0 && index < order.Count ? order[index] : null;
        }
        private static bool Wanted => !shuttingDown && inputHookReady && Plugin.Self != null &&
            Plugin.Self.FeatureEnabled && Plugin.Self.Ready && SingletonMonoBehaviour<BattleManager>.Instance != null &&
            SingletonMonoBehaviour<BattleManager>.Instance.GetCurretState() == BattleManager.BATTLE_STATE.ON_BASE;

        internal static void OnFeatureChanged()
        {
            foreach (var view in Views.Values.ToList())
            {
                if (view.UI == null) { DismissConfirmation(view); Views.Remove(view.UI); continue; }
                view.Revision = -1;
                Synchronize(view);
            }
        }

        internal static void Shutdown()
        {
            shuttingDown = true;
            foreach (var view in Views.Values.ToList())
            {
                DismissConfirmation(view);
                if (view.UI != null) RemoveRows(view, true);
            }
            Views.Clear();
            NativeMissionGuide.Shutdown();
            NativeMissionMaterialMarks.Shutdown();
        }

        private static void Report(Exception error)
        {
            if (reported) return;
            reported = true;
            Debug.LogWarning("[ANOMALITH Wishlist] Native Mission integration: " + error);
        }

        private static void Loaded(QuestUI ui)
        {
            if (shuttingDown) return;
            SuppressCancellation();
            if (!Views.TryGetValue(ui, out var view)) Views.Add(ui, view = new View { UI = ui });
            var lifetime = ui.GetComponent<MissionWishlistLifetime>();
            if (lifetime == null) lifetime = ui.gameObject.AddComponent<MissionWishlistLifetime>();
            lifetime.UI = ui;
            view.Revision = -1;
            Synchronize(view);
        }

        private static void Synchronize(View view)
        {
            try
            {
                if (!Wanted || view.Faulted)
                {
                    DismissConfirmation(view);
                    if (view.Targets.Count > 0 || view.Section != null) RemoveRows(view, true);
                    view.Revision = -1;
                    return;
                }
                if (view.Confirming) return;
                var plugin = Plugin.Self;
                if (view.Revision == plugin.Revision) return;
                var stages = plugin.Stages.Where(s => !s.Complete && plugin.SelectedTargets.TryGetValue(s.Key, out int q) && q > 0)
                    .OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
                var keys = stages.Select(s => s.Key).ToList();
                if (!view.Keys.SequenceEqual(keys))
                {
                    string selected = Selected(view.UI);
                    bool selectedWish = selected != null && view.Targets.ContainsKey(selected);
                    int previousIndex = (int)CursorField.GetValue(view.UI);
                    RemoveRows(view, false);
                    if (stages.Count > 0) AddRows(view, stages);
                    var order = Order(view.UI);
                    if (order != null && order.Count > 0)
                    {
                        int index = selected == null ? -1 : order.IndexOf(selected);
                        bool needsSelection = selectedWish || index < 0;
                        if (index < 0) index = Mathf.Clamp(previousIndex, 0, order.Count - 1);
                        CursorField.SetValue(view.UI, index);
                        if (needsSelection)
                        {
                            foreach (var row in Rows(view.UI).Values) row.Unselect();
                            Rows(view.UI)[order[index]].Select();
                            SetTextMethod.Invoke(view.UI, new object[] { order[index] });
                            Rebuild(view);
                            ScrollMethod.Invoke(view.UI, new object[] { Rows(view.UI)[order[index]].GetComponent<RectTransform>() });
                        }
                    }
                }
                else
                {
                    foreach (var stage in stages)
                    {
                        var row = Rows(view.UI)[view.Prefix + stage.Key];
                        SetWishRow(row, stage);
                    }
                    string selected = Selected(view.UI);
                    if (selected != null && view.Targets.TryGetValue(selected, out string key)) ShowDetail(view, key, false);
                }
                view.Revision = plugin.Revision;
            }
            catch (Exception error)
            {
                // Fail closed: remove all synthetic IDs, even if prefab construction failed halfway.
                view.Faulted = true;
                RemoveRows(view, true);
                Report(error);
            }
        }

        private static void AddRows(View view, List<RecipeStage> stages)
        {
            var ui = view.UI;
            var nativeContent = Field<RectTransform>(ui, "questListContent");
            var sourceRows = Field<RectTransform>(ui, "m_SubQuestRoot");
            Transform sourceSection = sourceRows.parent;
            // Clone an actual Mission section (heading, spacing, list layout), never
            // a generic SystemUI panel/font. Refuse an unexpected prefab hierarchy.
            if (sourceSection == null || sourceSection == nativeContent || !sourceSection.IsChildOf(nativeContent))
                throw new InvalidOperationException("Mission section hierarchy is not supported.");
            while (sourceSection.parent != nativeContent) sourceSection = sourceSection.parent;
            if (Field<RectTransform>(ui, "m_MainQuestRoot").IsChildOf(sourceSection) ||
                Field<RectTransform>(ui, "m_DoneQuestRoot").IsChildOf(sourceSection))
                throw new InvalidOperationException("Mission sections do not have separate native containers.");
            var path = new List<int>();
            for (Transform t = sourceRows; t != sourceSection; t = t.parent) path.Add(t.GetSiblingIndex());
            view.Section = UnityEngine.Object.Instantiate(sourceSection.gameObject, nativeContent, false);
            view.Section.name = "Wishlist Mission Section";
            view.Section.SetActive(false);
            Transform content = view.Section.transform;
            for (int i = path.Count - 1; i >= 0; i--) content = content.GetChild(path[i]);
            view.Content = (RectTransform)content;
            foreach (Transform child in content.Cast<Transform>().ToList()) Retire(child.gameObject);
            var heading = view.Section.GetComponentsInChildren<TextMeshProUGUI>(true)
                .FirstOrDefault(t => !t.transform.IsChildOf(content));
            if (heading == null) throw new InvalidOperationException("Mission section heading is unavailable.");
            SetLiteral(heading, "心愿单");
            heading.raycastTarget = false;
            view.Section.transform.SetAsLastSibling();
            var prefab = Field<QuestElement>(ui, "questElementPrefab");
            foreach (var stage in stages)
            {
                string id = view.Prefix + stage.Key;
                if (Rows(ui).ContainsKey(id)) throw new InvalidOperationException("Duplicate UI-only wishlist identity.");
                var row = UnityEngine.Object.Instantiate(prefab, content, false);
                row.name = "Wishlist Mission Row";
                row.m_Id = id;
                row.m_ElementId = id;
                row.InitializeButton(id);
                row.m_SelectEvent = null; // Native QuestUI owns selection/detail switching.
                row.m_SubmitEvent = ignored => SelectRow(view, id); // Click/confirm views; never removes.
                row.EnableNewIcon(false);
                row.SetDoneIcon(false);
                Field<GameObject>(row, "m_MainQuestIcon").SetActive(false);
                Field<GameObject>(row, "m_SubQuestIcon").SetActive(false);
                SetWishRow(row, stage);
                row.transform.localScale = Vector3.one;
                row.Unselect();
                row.gameObject.SetActive(true);
                view.Targets.Add(id, stage.Key);
                view.Keys.Add(stage.Key);
                Rows(ui).Add(id, row);
                Order(ui).Add(id);
            }
            for (Transform t = content; t != nativeContent; t = t.parent) t.gameObject.SetActive(true);
            Rebuild(view);
        }

        private static void SetWishRow(QuestElement row, RecipeStage stage)
        {
            var text = Field<TextMeshProUGUI>(row, "text");
            SetLiteral(text, stage.Name); // Preserve the native quest-name text column.
            // Keep the aggregate state, but position it in the native quest icon slot.
            NativeMissionRowMark.Bind(row, Plugin.Self.TargetMarker(stage.Key));
        }

        private static void SelectRow(View view, string id)
        {
            if (view.UI == null || !view.Targets.TryGetValue(id, out string key)) return;
            var order = Order(view.UI);
            int index = order.IndexOf(id);
            if (index < 0) return;
            string previous = Selected(view.UI);
            if (previous != null && Rows(view.UI).TryGetValue(previous, out var old)) old.Unselect();
            CursorField.SetValue(view.UI, index);
            Rows(view.UI)[id].Select();
            ShowDetail(view, key, true);
        }

        private static void RemoveRows(View view, bool selectReal)
        {
            DismissConfirmation(view);
            string selected = Selected(view.UI);
            bool selectedWish = selected != null && view.Targets.ContainsKey(selected);
            int previousIndex = (int)CursorField.GetValue(view.UI);
            RestoreDetail(view);
            var rows = Rows(view.UI);
            var tabs = (Dictionary<QuestManager.QuestType, List<string>>)TabsField.GetValue(view.UI);
            foreach (string id in view.Targets.Keys)
            {
                foreach (var order in tabs.Values) order.Remove(id);
                if (rows.TryGetValue(id, out var row))
                {
                    rows.Remove(id);
                    if (row != null) Retire(row.gameObject);
                }
            }
            view.Targets.Clear();
            view.Keys.Clear();
            if (view.Section != null) Retire(view.Section);
            view.Section = null;
            view.Content = null;
            var list = Order(view.UI);
            if (list != null)
            {
                int index = list.Count == 0 ? 0 : Mathf.Clamp(previousIndex, 0, list.Count - 1);
                CursorField.SetValue(view.UI, index);
                if (selectReal && selectedWish && list.Count > 0)
                {
                    rows[list[index]].Select();
                    SetTextMethod.Invoke(view.UI, new object[] { list[index] });
                }
            }
            Rebuild(view);
        }

        private static void Retire(GameObject obj)
        {
            obj.SetActive(false);
            obj.transform.SetParent(null, false); // Delayed Destroy must not count as a native layout child.
            UnityEngine.Object.Destroy(obj);
        }
        private static void Rebuild(View view)
        {
            if (view.Content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(view.Content);
            var content = Field<RectTransform>(view.UI, "questListContent");
            if (content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        }

        private static void SetLiteral(TextMeshProUGUI text, string value)
        {
            if (text == null) return;
            var localizer = text.GetComponent("nnLocalizeDataUGUI");
            if (localizer != null) AccessTools.Field(localizer.GetType(), "EnableStringTabel")?.SetValue(localizer, false);
            value = Plugin.Escape(value ?? "");
            NativeWishlistFonts.Apply(text, value);
            text.text = value;
        }

        private static void ShowDetail(View view, string key, bool resetScroll)
        {
            var plugin = Plugin.Self;
            var stage = plugin?.Stages.FirstOrDefault(s => s.Key == key && !s.Complete);
            if (stage == null) return;
            var detail = Field<QuestDeteilInfo>(view.UI, "m_QuestDeteil");
            if (view.Detail == null) view.Detail = new DetailSnapshot(view.UI, detail);
            SetLiteral(Field<TextMeshProUGUI>(detail, "m_QuestTitle"), stage.Name);
            var lines = new StringBuilder();
            var marks = new List<NativeMissionMaterialMarks.Mark>();
            int batches = stage.Repeatable && plugin.SelectedTargets.TryGetValue(key, out int q) ? q : 1;
            foreach (var material in stage.Materials.Where(x => x.Value > 0))
            {
                long required = checked(material.Value * batches);
                plugin.Stocks.TryGetValue(material.Key, out var stock);
                bool known = stock != null && stock.Ready;
                var allocation = known ? DemandEngine.AllocateMaterial(plugin.Stages, plugin.SelectedTargets,
                    material.Key, stock.Owned).FirstOrDefault(x => x.Stage.Key == key) : null;
                bool stocked = allocation != null && allocation.Remaining == 0;
                if (lines.Length > 0) lines.Append('\n');
                marks.Add(new NativeMissionMaterialMarks.Mark(lines.Length, known ? (stocked ? 2 : 1) : 0));
                lines.Append(NativeMissionMaterialMarks.Gutter).Append(GameRecipes.ItemName(material.Key))
                    .Append("  需要 ").Append(required)
                    .Append(" / 库存 ").Append(known ? stock.Owned.ToString() : "—");
            }
            if (lines.Length == 0)
            {
                marks.Add(new NativeMissionMaterialMarks.Mark(0, 2));
                lines.Append(NativeMissionMaterialMarks.Gutter).Append("无需材料");
            }
            var body = Field<TextMeshProUGUI>(detail, "m_QuestDeteil");
            SetLiteral(body, lines.ToString());
            NativeMissionMaterialMarks.Bind(body, marks);
            Field<GameObject>(detail, "m_TitleRoot").SetActive(true);
            Field<GameObject>(detail, "m_DeteilRoot").SetActive(true);
            detail.EnableTarget(false);
            detail.EnableReward(false);
            detail.EnaboleTrackingIcon(false);
            foreach (string name in new[] { "m_QuestTitleMainIcon", "m_QuestTitleSubIcon", "m_QuestTitleCheckIcon" })
            {
                var icon = Field<GameObject>(view.UI, name);
                if (icon != null) icon.SetActive(false);
            }
            var scroll = Field<ScrollRect>(view.UI, "m_DeteilScroll");
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            if (resetScroll)
            {
                AccessTools.Field(typeof(QuestUI), "m_DeteilScrollRate").SetValue(view.UI, 1f);
                scroll.verticalNormalizedPosition = 1f;
            }
            if (resetScroll)
            {
                // Do not advertise quest tracking/reward actions on a wishlist entry.
                var guides = Field<List<ButtonGuideText.ButtonGuideData>>(view.UI, "m_OperationGuideDataList");
                PauseMenu.Instance.SetOperationGuide(guides.Where(g => g.m_ButtonInputs == null ||
                    !g.m_ButtonInputs.Any(b => b != null && (b.m_Input == ButtonGuideText.ButtonFontInputData.SUBMIT ||
                    b.m_Input == ButtonGuideText.ButtonFontInputData.BUTTON_UP))).ToList());
            }
            NativeMissionGuide.Show(view.UI);
        }

        private static void RestoreDetail(View view)
        {
            if (view.UI != null)
            {
                var detail = Field<QuestDeteilInfo>(view.UI, "m_QuestDeteil");
                if (detail != null) NativeMissionMaterialMarks.Hide(Field<TextMeshProUGUI>(detail, "m_QuestDeteil"));
                NativeMissionGuide.Hide(view.UI);
            }
            view.Detail?.Restore();
            view.Detail = null;
        }

        private static void SuppressCancellation()
        {
            inputBlockedThroughFrame = Time.frameCount + 1;
            cancelNeedsRelease = true;
        }

        private static void ConfirmationClosed(View view, DialogGeneral dialog)
        {
            if (!ReferenceEquals(view.Confirmation, dialog)) return;
            foreach (var text in view.ConfirmationTexts) text.Restore();
            view.ConfirmationTexts.Clear();
            view.Confirmation = null;
            view.Confirming = false;
            SuppressCancellation();
        }

        private static void DismissConfirmation(View view)
        {
            var dialog = view.Confirmation;
            if (ReferenceEquals(dialog, null)) return;
            // Restore borrowed native font references before helper shutdown, even
            // when Unity delays destruction of the dialog until the end of the frame.
            ConfirmationClosed(view, dialog);
            if (dialog == null) return;
            dialog.OnSubmit = null;
            dialog.OnCancel = null;
            dialog.IsDisableOperation = true;
            dialog.Close();
            dialog.gameObject.SetActive(false);
            dialog.Destroy(); // Native removal also releases DialogManager's stack entry.
        }

        internal static void OnUiDestroyed(QuestUI ui)
        {
            if (ReferenceEquals(ui, null) || !Views.TryGetValue(ui, out var view)) return;
            DismissConfirmation(view);
            RestoreDetail(view);
            Views.Remove(ui);
        }

        // Runs after the game's entire native hover/up/down/select/scroll block,
        // but BEFORE it resolves a quest ID or changes persistent tracking state.
        internal static bool HandleWishlistInput(QuestUI ui)
        {
            if (!Views.TryGetValue(ui, out var view)) return false;
            string id = Selected(ui);
            if (id == null || !view.Targets.TryGetValue(id, out string key)) return false;
            // Always consume this branch for synthetic IDs, even during shutdown.
            if (!Wanted || view.Confirming) return true;
            bool held = (Keyboard.current != null && Keyboard.current.vKey.isPressed) ||
                (Gamepad.current != null && Gamepad.current.rightStickButton.isPressed);
            if (!held && Time.frameCount > inputBlockedThroughFrame) cancelNeedsRelease = false;
            bool cancel = Application.isFocused && !cancelNeedsRelease && Time.frameCount > inputBlockedThroughFrame &&
                ((Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame) ||
                (Gamepad.current != null && Gamepad.current.rightStickButton.wasPressedThisFrame));
            if (cancel && DialogManager.IsParentDialogTop(ui.transform))
            {
                var stage = Plugin.Self.Stages.FirstOrDefault(s => s.Key == key && !s.Complete);
                if (stage != null)
                {
                    SuppressCancellation();
                    var dialog = DialogManager.CreateDialog<DialogGeneral>(DialogType.General);
                    view.Confirmation = dialog;
                    view.Confirming = true;
                    dialog.SetDialog(DialogGeneral.DialogType.YesNo, "取消收藏", "取消收藏「" + Plugin.Escape(stage.Name) + "」？",
                        type =>
                        {
                            SuppressCancellation();
                            if (type == DialogGeneral.ButtonType.YES && ReferenceEquals(view.Confirmation, dialog) &&
                                ui != null && Views.ContainsKey(ui) && Plugin.Self != null && Plugin.Self.FeatureEnabled)
                            {
                                Plugin.Self.RemoveTarget(key);
                                Plugin.Self.RequestRefresh();
                                view.Revision = -1; // Refresh after native modal close, not inside its Submit callback.
                            }
                        }, SuppressCancellation);
                    view.ConfirmationTexts.Add(new TextSnapshot(Field<TextMeshProUGUI>(dialog, "m_Caption")));
                    view.ConfirmationTexts.Add(new TextSnapshot(Field<TextMeshProUGUI>(dialog, "m_Message")));
                    SetLiteral(Field<TextMeshProUGUI>(dialog, "m_Caption"), "取消收藏");
                    SetLiteral(Field<TextMeshProUGUI>(dialog, "m_Message"), "取消收藏「" + stage.Name + "」？");
                    dialog.SetButtonCursor(DialogGeneral.ButtonType.NO);
                    dialog.Open(() => ConfirmationClosed(view, dialog));
                }
            }
            else
            {
                // Confirm and mouse submit are intentionally not destructive.
                ui.StateChangeCheck();
            }
            return true;
        }

        private sealed class TextSnapshot
        {
            private readonly TextMeshProUGUI text;
            private readonly string value;
            private readonly TMP_FontAsset font;
            private readonly Material material;
            private readonly Component localizer;
            private readonly FieldInfo localizeField;
            private readonly object localizeValue;
            internal TextSnapshot(TextMeshProUGUI source)
            {
                text = source;
                value = source.text;
                font = source.font;
                material = source.fontSharedMaterial;
                localizer = source.GetComponent("nnLocalizeDataUGUI");
                if (localizer != null)
                {
                    localizeField = AccessTools.Field(localizer.GetType(), "EnableStringTabel");
                    localizeValue = localizeField?.GetValue(localizer);
                }
            }
            internal void Restore()
            {
                if (text == null) return;
                text.font = font;
                text.fontSharedMaterial = material;
                text.text = value;
                if (localizer != null && localizeField != null) localizeField.SetValue(localizer, localizeValue);
            }
        }

        private sealed class DetailSnapshot
        {
            private readonly List<TextSnapshot> texts = new List<TextSnapshot>();
            private readonly Dictionary<GameObject, bool> active = new Dictionary<GameObject, bool>();
            internal DetailSnapshot(QuestUI ui, QuestDeteilInfo detail)
            {
                texts.Add(new TextSnapshot(Field<TextMeshProUGUI>(detail, "m_QuestTitle")));
                texts.Add(new TextSnapshot(Field<TextMeshProUGUI>(detail, "m_QuestDeteil")));
                foreach (string name in new[] { "m_TitleRoot", "m_DeteilRoot", "m_TargetRoot", "m_RewardRoot", "m_TrackingIcon" })
                    Capture(Field<GameObject>(detail, name));
                foreach (string name in new[] { "m_QuestTitleMainIcon", "m_QuestTitleSubIcon", "m_QuestTitleCheckIcon" })
                    Capture(Field<GameObject>(ui, name));
            }
            private void Capture(GameObject obj) { if (obj != null) active[obj] = obj.activeSelf; }
            internal void Restore()
            {
                foreach (var text in texts) text.Restore();
                foreach (var pair in active) if (pair.Key != null) pair.Key.SetActive(pair.Value);
            }
        }

        [HarmonyPatch(typeof(QuestElement), "Select")]
        private static class RealSelectionPatch
        {
            private static void Prefix(QuestElement __instance)
            {
                // Restore before the native row's m_SelectEvent updates quest icons;
                // restoring later in SetQuestText would overwrite that native update.
                foreach (var view in Views.Values)
                {
                    if (view.UI == null || view.Detail == null || view.Targets.ContainsKey(__instance.m_Id)) continue;
                    if (Rows(view.UI).TryGetValue(__instance.m_Id, out var row) && row == __instance)
                        RestoreDetail(view);
                }
            }
        }

        [HarmonyPatch(typeof(QuestUI), "DataLoad")]
        private static class LoadPatch
        {
            private static void Prefix(QuestUI __instance)
            {
                if (Views.TryGetValue(__instance, out var view)) RemoveRows(view, false);
            }
            private static void Postfix(QuestUI __instance) => Loaded(__instance);
        }

        [HarmonyPatch(typeof(QuestUI), "DeleteQuestList")]
        private static class ClearPatch
        {
            private static void Prefix(QuestUI __instance)
            {
                if (!Views.TryGetValue(__instance, out var view)) return;
                RemoveRows(view, false);
                Views.Remove(__instance);
            }
        }

        [HarmonyPatch(typeof(QuestUI), "Calculation")]
        private static class TickPatch
        {
            private static void Prefix(QuestUI __instance)
            {
                if (Views.TryGetValue(__instance, out var view)) Synchronize(view);
            }
        }

        [HarmonyPatch(typeof(QuestUI), "SetQuestText")]
        private static class DetailPatch
        {
            private static bool Prefix(QuestUI __instance, string questID)
            {
                if (!Views.TryGetValue(__instance, out var view)) return true;
                if (view.Targets.TryGetValue(questID, out string key))
                {
                    ShowDetail(view, key, true);
                    return false;
                }
                RestoreDetail(view);
                return true; // Every real quest executes its original method unchanged.
            }
        }

        [HarmonyPatch(typeof(QuestUI), "StateUpdate")]
        private static class InputPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                var code = instructions.ToList();
                var lookup = AccessTools.Method(typeof(QuestManager), "GetCurrentMainQuestInfo");
                var matches = Enumerable.Range(1, code.Count - 1).Where(i => code[i].Calls(lookup)).ToList();
                // Expected stack-neutral boundary: call get_Instance; callvirt GetCurrentMainQuestInfo.
                // If a future game differs, leave IL unchanged AND do not insert wishlist rows.
                if (matches.Count != 1 || !(code[matches[0] - 1].operand is MethodInfo getter) ||
                    getter.Name != "get_Instance" || getter.ReturnType != typeof(QuestManager))
                {
                    inputHookReady = false;
                    Report(new InvalidOperationException("Mission input boundary did not match; integration remains inactive."));
                    return code;
                }
                int index = matches[0] - 1;
                var resume = generator.DefineLabel();
                var first = new CodeInstruction(OpCodes.Ldarg_0);
                // Bundled HarmonyX targets mscorlib Label while this mod targets
                // netstandard; use the non-generic collection bridge for its label lists.
                var originalLabels = (IList)InstructionLabels.GetValue(code[index]);
                var entryLabels = (IList)InstructionLabels.GetValue(first);
                foreach (object label in originalLabels) entryLabels.Add(label);
                first.blocks.AddRange(code[index].blocks);
                originalLabels.Clear();
                code[index].blocks.Clear();
                originalLabels.Add(resume);
                code.InsertRange(index, new[]
                {
                    first,
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NativeMissionWishlist), nameof(HandleWishlistInput))),
                    new CodeInstruction(OpCodes.Brfalse, resume),
                    new CodeInstruction(OpCodes.Ret)
                });
                inputHookReady = true;
                return code;
            }
        }
    }

    internal sealed class MissionWishlistLifetime : MonoBehaviour
    {
        internal QuestUI UI;
        private void OnDestroy() => NativeMissionWishlist.OnUiDestroyed(UI);
    }
}
