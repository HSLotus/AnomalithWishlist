using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AnomalithWishlist
{
    internal static class GameRecipes
    {
        internal static string Text(string table, string id, string fallback = null)
        {
            if (StringManager.instance != null)
            {
                var value = StringManager.instance.GetString(table, id);
                if (!string.IsNullOrEmpty(value) && !value.Contains("NotFound")) return value;
            }
            return string.IsNullOrEmpty(fallback) ? id : fallback;
        }
        internal static string ItemName(string id) => Text("ItemName", id);
        internal static string Owner(DevelopmetData data, string weapon = null)
        {
            if (data is WeaponParamReinforceData p) return "reinforce/" + (weapon ?? p.m_WeaponId) + "/" + data.m_ItemId;
            return (data is WeaponDevData ? "weapon/" : data is ArmorDevData ? "armor/" : data is AttachmentDevData ? "attachment/" : "consumable/") + data.m_ItemId;
        }
        internal static string StageKey(DevelopmetData data, string weapon = null) => Owner(data, weapon) + "/" + (data is ConsumablesDevData ? 0 : data.GetDevLevel() + 1);
        internal static string SkillKey(string tree) => "skill/" + tree;
        internal static bool Ready => SingletonMonoBehaviour<Inventory>.Instance != null && WeaponDevDatas.instance != null &&
            ArmorDevDatas.instance != null && AttachmentDevDatas.instance != null && ConsumablesDevDatas.instance != null &&
            WeaponReinforceDatas.instance != null && SkillDatas.instance != null &&
            SkillDatas.instance.GetAllSkillTreeList().Count > 0 && WeaponDevDatas.instance.GetAllItemDevList().Count > 0;

        internal static List<RecipeStage> Read()
        {
            if (!Ready) throw new InvalidOperationException("Game databases are not ready");
            var result = new List<RecipeStage>();
            AddDevelopment(result, WeaponDevDatas.instance.GetAllItemDevList(), "武器开发");
            AddDevelopment(result, ArmorDevDatas.instance.GetAllItemDevList(), "装备开发／强化");
            AddDevelopment(result, AttachmentDevDatas.instance.GetAllItemDevList(), "配件开发");
            AddDevelopment(result, ConsumablesDevDatas.instance.GetAllItemDevList(), "消耗品制作（可重复）");
            // Iterate the reinforcement database itself, not just visible/owned weapons.
            var reinforcement = (Dictionary<string, WeaponReinforceParams>)AccessTools.Field(typeof(WeaponReinforceDatas), "m_AllWeaponReinforceData").GetValue(WeaponReinforceDatas.instance);
            foreach (var pair in reinforcement)
                AddDevelopment(result, pair.Value.GetAllItemDevList(), "武器分项强化", pair.Key);
            foreach (var data in SkillDatas.instance.GetAllSkillTreeList())
            {
                // Runtime reuses the first tree node as the mutable current-level record.
                // Display the immutable SO stage ordinal, not that mutable value.
                var source = SkillDatas.instance.m_DataSO.m_SkillDatas.FirstOrDefault(x => x.m_SkillTreeId == data.m_SkillTreeId);
                int level = (source ?? data).m_SkillLevel;
                var s = new RecipeStage { Key = SkillKey(data.m_SkillTreeId), Owner = SkillKey(data.m_SkillId),
                    Name = Text("SYSTEM", data.m_SkillId, data.m_SkillName), Category = "技能学习／提升",
                    Complete = SkillDatas.instance.IsLearnedSkillTree(data.m_SkillTreeId), Money = data.m_NeedMoney, FromLevel = level, ToLevel = level + 1 };
                AddCosts(s, data.m_NeedMatDataList);
                result.Add(s);
            }
            return result;
        }
        private static void AddDevelopment(List<RecipeStage> result, List<DevelopmetData> dataList, string category, string weapon = null)
        {
            if (dataList == null) return;
            foreach (var data in dataList)
            {
                bool repeat = data is ConsumablesDevData;
                int stages = repeat ? 1 : Math.Max(0, data.GetMaxDevLevel());
                for (int from = 0; from < stages; from++)
                {
                    string owner = Owner(data, weapon);
                    string name = weapon == null ? ItemName(data.m_ItemId) : ItemName(weapon) + " · " + Text("ParamName", data.m_ItemId.Replace("_Lv", ""), data.m_ItemName);
                    var stage = new RecipeStage { Key = owner + "/" + (repeat ? 0 : from + 1), Owner = owner, Name = name,
                        Category = category, FromLevel = from, ToLevel = from + 1, Repeatable = repeat,
                        Complete = !repeat && data.GetDevLevel() > from };
                    var rows = data.m_LevelNeedMatDatas;
                    if (rows != null && rows.Count > 0)
                    {
                        var costs = rows[Math.Min(from, rows.Count - 1)];
                        AddCosts(stage, costs.m_NeedMatDatas);
                        stage.Money = costs.m_NeedMoney;
                    }
                    result.Add(stage);
                }
            }
        }
        private static void AddCosts(RecipeStage stage, List<NeedMatData> costs)
        {
            if (costs == null) return;
            foreach (var cost in costs)
            {
                if (cost == null || string.IsNullOrEmpty(cost.m_MatId) || cost.m_Num < 0)
                    throw new InvalidOperationException("Invalid material data in " + stage.Key);
                if (cost.m_Num == 0) continue;
                string id = Inventory.ConvertItemID(cost.m_MatId);
                stage.Materials.TryGetValue(id, out long previous);
                stage.Materials[id] = checked(previous + cost.m_Num);
            }
        }

        // R3 already has native functions in equipment/drone/repository screens. Never intercept it there.
        internal static bool IsRecipeMenu(DevMenuBase menu) => menu is WeaponDevMenu || menu is ArmorDevMenu ||
            menu is AttachmentDevMenu || menu is ConsumablesDevMenu || menu is WeaponReinforceMenu;
        internal static string CurrentSelection()
        {
            if (DialogManager.IsInvalid) return null;
            var top = DialogManager.GetTopDialog();
            if (top is SkillTreeManager tree && tree.isActiveAndEnabled && !tree.IsDisable &&
                tree.m_IsOperation && tree.m_CurrentParts != null)
                return SkillKey(tree.m_CurrentParts.GetTreeId());
            if (top is DevMenuBase menu && IsRecipeMenu(menu) && menu.isActiveAndEnabled &&
                !(bool)AccessTools.Method(menu.GetType(), "GetDisable").Invoke(menu, null) &&
                !(bool)AccessTools.Field(typeof(DevMenuTabBase), "m_IsTabSwitch").GetValue(menu))
            {
                var selector = (DevelopItemSelector)AccessTools.Field(typeof(DevMenuBase), "m_CurrentSelector").GetValue(menu);
                var data = selector?.GetDevData();
                if (data == null || (!(data is ConsumablesDevData) && data.IsMaxDevLevel())) return null;
                return StageKey(data, (menu as WeaponReinforceMenu)?.GetWeaponDevData()?.m_ItemId);
            }
            return null;
        }
    }
}
