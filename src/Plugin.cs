using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AnomalithWishlist
{
    [BepInPlugin(Id, "ANOMALITH 心愿单", Version)]
    [BepInProcess("Anomalith.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.anomalith.wishlist";
        public const string Version = "1.4.5";
        public const string SupportedHash = "de282e73a6e4b53a60a84e53ad30699a1fc5e9e90d0157e8a8eb37c6e7c81fe3";
        internal static Plugin Self;
        internal bool Ready { get; private set; }
        internal bool FeatureEnabled => isActiveAndEnabled && featureEnabled != null && featureEnabled.Value;
        internal bool AllMode => allMode != null && allMode.Value;
        internal List<RecipeStage> Stages = new List<RecipeStage>();
        internal Dictionary<string, MaterialDemand> Demands = new Dictionary<string, MaterialDemand>();
        internal Dictionary<string, StockCounts> Stocks = new Dictionary<string, StockCounts>();
        private Dictionary<string, MaterialDemand> manualDemands = new Dictionary<string, MaterialDemand>();
        private Dictionary<string, int> targetStates = new Dictionary<string, int>();
        internal int Revision { get; private set; }
        internal string CurrentKey { get; private set; }
        private ConfigEntry<bool> allMode, featureEnabled;
        private Harmony harmony;
        private readonly Dictionary<string, int> selected = new Dictionary<string, int>(StringComparer.Ordinal);
        private string statePath;
        private float nextRefresh, lastError = -20;

        private void Awake()
        {
            Self = this;
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(Path.Combine(Paths.ManagedPath, "Assembly-CSharp.dll")))
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (actual != SupportedHash) throw new InvalidOperationException("游戏版本不匹配，安全停用。SHA256=" + actual);
                }
                featureEnabled = Config.Bind("Wishlist", "Enabled", false, "模组总开关，默认关闭；在原生设置的Mod配置大项阅读提示后开启。");
                allMode = Config.Bind("Wishlist", "AllRemainingContent", false, "额外开启全局材料提醒；不影响手动心愿单，两者可同时显示。");
                // One-time onboarding for 1.2: old enabled=true must not silently bypass
                // the new default-off Mod category and its risk notice. Keep all targets.
                var nativeConfigRevision = Config.Bind("Wishlist", "NativeModConfigRevision", 0, "内部迁移标记：1表示已启用默认关闭的独立Mod配置流程。");
                if (nativeConfigRevision.Value < 1)
                {
                    featureEnabled.Value = false;
                    nativeConfigRevision.Value = 1;
                    Config.Save();
                }
                statePath = Path.Combine(Paths.ConfigPath, "AnomalithWishlist.targets.json");
                LoadTargets();
                harmony = new Harmony(Id);
                harmony.PatchAll(typeof(Plugin).Assembly);
                Logger.LogInfo("Native wishlist " + Version + "; version guard PASS; R3/V favorite, default-off Mod settings, additive global reminders. No standalone window.");
            }
            catch (Exception e)
            {
                Logger.LogError(e); harmony?.UnpatchSelf(); enabled = false; Self = null;
            }
        }
        private void LoadTargets()
        {
            if (!File.Exists(statePath)) return;
            try
            {
                var data = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(statePath));
                if (data == null) throw new InvalidDataException("Empty wishlist file");
                foreach (var p in data)
                    if (!string.IsNullOrEmpty(p.Key) && p.Value > 0 && p.Value <= 9999) selected[p.Key] = p.Value;
            }
            catch (Exception e)
            {
                File.Copy(statePath, statePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
                Logger.LogWarning("心愿单文件无法读取，已保留备份：" + e.Message);
            }
        }
        private void SaveTargets()
        {
            try { AtomicJson(statePath, selected, statePath + ".bak"); }
            catch (Exception e) { Logger.LogError(e); Notify("收藏保存失败，本次选择只保留在内存中。请检查BepInEx日志。"); }
        }
        private void LateUpdate()
        {
            try
            {
                if (!FeatureEnabled) { Ready = false; CurrentKey = null; NativePickupNotifications.Reset(); return; }
                if (Time.unscaledTime >= nextRefresh) Refresh();
                if (!Ready) return;
                NativePickupNotifications.Tick();
                CurrentKey = GameRecipes.CurrentSelection();
                if (!Application.isFocused) return;
                var keyboard = Keyboard.current;
                bool favorite = keyboard != null && keyboard.vKey.wasPressedThisFrame;
                favorite |= Gamepad.current != null && Gamepad.current.rightStickButton.wasPressedThisFrame;
                if (favorite && CurrentKey != null) ToggleCurrent();
            }
            catch (Exception e)
            {
                Ready = false; Demands.Clear(); Stocks.Clear(); CurrentKey = null;
                if (Time.unscaledTime - lastError > 10) { Logger.LogError(e); lastError = Time.unscaledTime; }
            }
        }
        private void Refresh()
        {
            nextRefresh = Time.unscaledTime + 0.3f;
            if (!GameRecipes.Ready) { Ready = false; Demands.Clear(); Stocks.Clear(); CurrentKey = null; return; }
            Stages = GameRecipes.Read();
            manualDemands = DemandEngine.Calculate(Stages, selected, false);
            Demands = AllMode ? DemandEngine.Calculate(Stages, selected, true) : manualDemands;
            var counts = new Dictionary<string, StockCounts>();
            foreach (string id in Demands.Keys) counts[id] = StockReader.Read(id);
            // Readiness is catalog-wide only. A dragged/uncertain material disables ITS
            // quantity/marker, not unrelated tooltips or the ability to bookmark a recipe.
            Stocks = counts; Ready = true; Revision++;
            targetStates = Stages.Where(x => !x.Complete && IsSelected(x.Key)).ToDictionary(x => x.Key, x => 2);
            foreach (var entry in manualDemands.Where(x => x.Value.Required > 0))
            {
                var stock = counts[entry.Key];
                foreach (var line in DemandEngine.AllocateMaterial(Stages, selected, entry.Key, stock.Ready ? stock.Owned : 0))
                    if (!stock.Ready || line.Remaining > 0) targetStates[line.Stage.Key] = 1;
            }
        }
        private static void AtomicJson(string path, object data, string backup = null)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.Indented));
            if (File.Exists(path)) File.Replace(tmp, path, backup); else File.Move(tmp, path);
        }
        internal void SetFeatureEnabled(bool value)
        {
            if (featureEnabled == null || featureEnabled.Value == value) return;
            try { featureEnabled.Value = value; Config.Save(); }
            catch (Exception e) { Notify("设置已在内存中更改，但无法保存配置：" + e.Message); }
            Revision++; nextRefresh = 0;
            NativeMissionWishlist.OnFeatureChanged();
            if (!value)
            {
                Ready = false; CurrentKey = null;
                NativePickupNotifications.Reset(); NativeStoragePopups.Shutdown();
                NativeDescriptions.RestoreAll(); NativeWishlistUI.HideAll();
            }
        }
        internal void SetAllMode(bool value)
        {
            if (allMode == null || allMode.Value == value) return;
            try { allMode.Value = value; Config.Save(); }
            catch (Exception e) { Notify("模式已在内存中更改，但无法保存配置：" + e.Message); }
            Revision++; nextRefresh = 0;
            if (FeatureEnabled) Refresh();
        }
        internal IReadOnlyDictionary<string, int> SelectedTargets => selected;
        internal IReadOnlyDictionary<string, MaterialDemand> ManualDemands => manualDemands;
        internal void RequestRefresh() => nextRefresh = 0;
        internal void RemoveTarget(string key)
        {
            if (!FeatureEnabled || key == null || !selected.Remove(key)) return;
            NativePickupNotifications.Reset();
            var stage = FindStage(key);
            SaveTargets(); Refresh();
            Notify("已取消收藏：" + (stage == null ? key : StageName(stage)));
        }
        internal bool IsSelected(string key) => key != null && selected.TryGetValue(key, out int q) && q > 0;
        internal int SelectedQuantity(string key) => key != null && selected.TryGetValue(key, out int q) ? q : 0;
        internal RecipeStage FindStage(string key) => key == null ? null : Stages.FirstOrDefault(x => x.Key == key);
        internal static string StageName(RecipeStage s) => s.Name + (s.Repeatable ? "（制作）" : " · 第" + s.ToLevel + "阶段");
        internal string MaterialText(string id, bool rich = true, bool shop = false)
        {
            if (!FeatureEnabled || !Ready || string.IsNullOrEmpty(id)) return "";
            id = Inventory.ConvertItemID(id);
            if (!Demands.TryGetValue(id, out var d) || !Stocks.TryGetValue(id, out var stock) || !stock.Ready) return "";
            var blocks = new List<string>();
            // Manual favorites are ALWAYS active. Global reminders are a separate view of
            // the same inventory, not another order to add to the manual requirement.
            if (manualDemands.TryGetValue(id, out var manual) && manual.Required > 0)
            {
                long manualRemaining = DemandEngine.Shortage(manual.Required, stock.Owned);
                var text = new StringBuilder("心愿单还需" + manualRemaining + "个");
                if (shop && !AllMode)
                    text.Append("\n合成需要 ").Append(manual.Required).Append(" 个，多余 ")
                        .Append(DemandEngine.Surplus(manual.Required, stock.Owned)).Append(" 个（仅心愿单）");
                var lines = DemandEngine.AllocateMaterial(Stages, selected, id, stock.Owned);
                foreach (var line in lines)
                    text.Append("\n- ").Append(StageName(line.Stage)).Append(line.Batches > 1 ? " ×" + line.Batches + "批" : "")
                        .Append(" 需要 ").Append(line.Remaining).Append(" 个").Append(line.Remaining == 0 ? "（已备齐）" : "");
                if (lines.Count > 1) text.Append("\n库存按上述目标顺序抵扣，不重复计入");
                blocks.Add(MaterialBlock(text.ToString(), manualRemaining, rich));
            }
            if (AllMode)
            {
                long globalRemaining = DemandEngine.Shortage(d.Required, stock.Owned);
                var text = new StringBuilder();
                if (shop)
                {
                    // Global finite demand already contains all selected finite stages.
                    // Add ONLY explicitly selected repeatable batches, never the whole
                    // manual demand again, and never treat unbounded future crafting as zero use.
                    long repeatRequired = 0;
                    foreach (var stage in Stages)
                        if (stage.Repeatable && !stage.Complete && selected.TryGetValue(stage.Key, out int batches) && batches > 0 &&
                            stage.Materials.TryGetValue(id, out long cost) && cost > 0)
                            repeatRequired = checked(repeatRequired + checked(cost * batches));
                    long required = checked(d.Required + repeatRequired);
                    globalRemaining = DemandEngine.Shortage(required, stock.Owned);
                    text.Append("合成需要 ").Append(required).Append(" 个，多余 ")
                        .Append(DemandEngine.Surplus(required, stock.Owned)).Append(" 个");
                    if (d.HasFiniteUse && d.Required == 0 && !d.HasRepeatableUse)
                        text.Append("\n所有该材料配方已经解锁（已收录有限配方）");
                    else
                    {
                        text.Append("\n全配方还需").Append(globalRemaining).Append("个");
                        if (repeatRequired > 0) text.Append("（含已收藏制作批次）");
                        if (d.HasFiniteUse && d.Required == 0) text.Append("\n有限开发／强化／技能用途已完成");
                    }
                    if (d.HasRepeatableUse)
                        text.Append("\n仍用于重复制作；未收藏批次未计入，多余不代表可全部出售");
                }
                else
                {
                    if (d.Required > 0) text.Append("全配方还需").Append(globalRemaining).Append("个").Append(globalRemaining == 0 ? "（材料已备齐）" : "");
                    else if (d.HasFiniteUse) text.Append("有限解锁／强化用途已完成");
                    else text.Append("无已收录的有限解锁／强化用途");
                    if (d.HasRepeatableUse) text.Append("\n仍用于重复制作，请按需保留");
                }
                blocks.Add(MaterialBlock(text.ToString(), globalRemaining, rich));
            }
            if (blocks.Count == 0) return "";
            if (stock.Staged > 0) blocks.Add("临时区另有" + stock.Staged + "个，请先收入背包");
            return (rich ? "\n\n" : "") + string.Join("\n\n", blocks);
        }
        private static string MaterialBlock(string text, long remaining, bool rich)
        {
            if (!rich) return text;
            return "<color=" + (remaining == 0 ? "#78E0A5" : "#FFD65C") + ">" + Escape(text) + "</color>";
        }
        internal static string Escape(string value) => value.Replace("<", "＜").Replace(">", "＞");
        // 0 hidden; 1 wanted/short; 2 stocked. Badges ALWAYS refer to manual favorites,
        // irrespective of whether additive global reminders are enabled.
        internal int MaterialMarker(string id)
        {
            if (!FeatureEnabled || !Ready || string.IsNullOrEmpty(id)) return 0;
            id = Inventory.ConvertItemID(id);
            if (!manualDemands.TryGetValue(id, out var demand) || demand.Required <= 0 || !Stocks.TryGetValue(id, out var stock) || !stock.Ready) return 0;
            return stock.Owned >= demand.Required ? 2 : 1;
        }
        internal int TargetMarker(string key)
        {
            if (!FeatureEnabled || !Ready || !IsSelected(key)) return 0;
            var stage = FindStage(key);
            if (stage == null || stage.Complete) return 0;
            return targetStates.TryGetValue(key, out int state) ? state : 0;
        }
        private void ToggleCurrent()
        {
            string key = GameRecipes.CurrentSelection();
            if (key == null) return;
            Refresh();
            if (!Ready) return;
            var stage = FindStage(key); if (stage == null) return;
            bool removed = selected.Remove(key);
            if (!removed)
            {
                if (stage.Complete) return;
                selected[key] = 1;
            }
            NativePickupNotifications.Reset();
            SaveTargets(); Refresh();
            Notify((removed ? "已取消收藏：" : "已加入心愿单：") + StageName(stage));
        }
        internal void Crafted(DevelopmetData data)
        {
            if (!FeatureEnabled || !(data is ConsumablesDevData)) return;
            string key = GameRecipes.StageKey(data);
            if (selected.TryGetValue(key, out int quantity))
            {
                if (quantity <= 1) selected.Remove(key); else selected[key] = quantity - 1;
                SaveTargets(); nextRefresh = 0;
            }
        }
        internal void Notify(string message)
        {
            Logger.LogInfo(message);
            try { SingletonMonoBehaviour<ItemLogger>.Instance?.InfoLog(() => message); }
            catch (Exception e) { Logger.LogDebug("Native notification unavailable: " + e.Message); }
        }
        private void OnEnable() { nextRefresh = 0; NativeMissionWishlist.OnFeatureChanged(); }
        private void OnDisable()
        {
            Ready = false; CurrentKey = null;
            NativePickupNotifications.Reset(); NativeStoragePopups.Shutdown();
            NativeDescriptions.RestoreAll(); NativeWishlistUI.HideAll(); NativeMissionWishlist.OnFeatureChanged();
        }
        private void OnDestroy()
        {
            NativeMissionWishlist.Shutdown(); NativePickupNotifications.Shutdown(); NativeStoragePopups.Shutdown();
            NativeSettings.Shutdown(); NativeDescriptions.RestoreAll(); NativeWishlistUI.Shutdown();
            NativeWishlistFonts.Shutdown();
            harmony?.UnpatchSelf(); if (Self == this) Self = null;
        }
    }
}
