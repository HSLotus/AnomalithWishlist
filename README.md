# ANOMALITH Wishlist · 材料心愿单

面向 **ANOMALITH Windows / Unity Mono 版**的非官方 BepInEx Mod。把开发、强化、技能所需的材料整理成心愿单，在游戏原生界面中查看需求、库存和备齐状态。

> **维护状态：功能已冻结，不再进行额外功能更新。**
>
> 本项目以现状开源，不承诺后续修复、游戏版本适配或技术支持。需要新功能或调整实现，请直接 Fork、自行修改和发布，无需逐次征求许可。采用 [MIT 许可证](<LICENSE>)，可以取用、修改、再分发和商用，但请保留版权及许可声明。

当前公开整理版本：**1.4.5**，基于 1.4.4 功能版本清理开发辅助内容。公开版本不含 F9 补料作弊入口、验收脚本或自动导出库存／配方的诊断功能。

## 功能

### 收藏与材料需求

- 在武器开发、装备开发／强化、配件开发、武器分项强化和技能树中，用 **V / R3** 收藏或取消当前阶段。
- 手动心愿单始终独立生效；可额外开启 **全局材料提醒**，查看所有已收录未完成有限阶段的需求。
- 物品原生介绍保留名称、描述、价格等内容，并追加相关心愿和剩余需求。
- 统计背包、仓库与来源可确认的手持物；未收入的临时区另行提示。
- 多个目标共用材料时按稳定的阶段键顺序分配库存，不让不同目标重复使用同一份材料。

### 原生界面

- **Mission 任务页**：基地任务列表底部增加“心愿单”分组，而非独立 Tab 或悬浮窗口。
- 左侧每行一个心愿，图标列和名称列沿用原生任务布局；右侧显示各材料的需要数量、实际库存和状态图标。
- **黄菱形**表示尚未确认全部备齐，**绿勾**表示按共享分配结果材料已齐；左侧显示整个目标的状态，右侧显示逐项状态。
- **V / R3 取消收藏**位于原生底部操作提示栏；取消需要确认，默认选“否”。鼠标点击和普通确认用于查看详情。
- 背包、仓库整理、仓库列表和实际 **工作室 → 售出**界面使用完整原生物品说明；长内容可滚动。
- 已完成的有限阶段隐藏收藏标记和清单行，不自动收藏下一阶段。
- 原生拾取提示附加黄／绿心愿标记；目标全部材料因实际获取而从不足变齐时，显示“xxx需求已经达到要求”。

### 商店说明

商店／工作室额外显示：

```text
合成需要 20 个，多余 15 个
全配方还需0个
```

这表示现有35个材料，其中20个仍需用于配方，**不是材料已无用途**。

- 全局提醒开启时：总需求为所有未完成有限阶段，加上明确收藏的重复制作批次；有限收藏目标不再重复相加。
- 全局提醒关闭时：只统计当前手动目标，并标注“仅心愿单”。
- 只有相关已收录有限阶段确实完成、且没有重复制作用途，才显示“所有该材料配方已经解锁（已收录有限配方）”。该文案依据实际完成进度，不是“已满足开发解锁条件”。
- 仍能重复制作时明确提醒未来批次未全部计入；**“多余”不是可以无条件出售的保证**。

### 配置

原生设置中有独立 **Mod配置** 大项。总开关和全局提醒均默认关闭；启用时展示功能／风险说明，并遵循游戏原生的应用／放弃更改流程。

收藏与配置保存在 BepInEx 配置目录，不向游戏任务进度中注册伪造任务。收藏在同一安装的不同存档间共享，完成情况和库存随当前存档读取；保留已完成目标的记录，是为了兼容读取较早存档。

## 安装与停用

1. **备份存档，完全退出游戏。**
2. 从 [BepInEx 官方发布页](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6)取得并按其说明安装 **BepInEx 5.4.23.6 Windows x64**。本仓库及构建脚本的发布包均不捆绑加载器。
3. 将自行编译的插件放到以下位置；不要同时保留多个副本：

```text
游戏目录/
└─ BepInEx/
   └─ plugins/
      └─ AnomalithWishlist/
         └─ AnomalithWishlist.dll
```

4. 自行启动游戏，在 **设置 → Mod配置 → 材料心愿单** 中开启并应用。

临时停用：关闭上述开关并应用。完全停用：退出游戏后移除本插件 DLL，勿删除其他 Mod 或共用加载器。

```text
BepInEx/config/local.anomalith.wishlist.cfg     # 独立设置
BepInEx/config/AnomalithWishlist.targets.json   # 收藏记录
BepInEx/LogOutput.log                          # 常规加载／错误日志
```

升级保留原配置与收藏。公开版本不再处理旧配置中的 F9 测试开关；停用或卸载也不会撤销历史版本曾写入存档的库存变化。

## 兼容范围

| 项目 | 目标 |
| --- | --- |
| 平台 | Windows x64 |
| 游戏运行时 | Unity 6000.3.18f1 / Mono，非 IL2CPP |
| 加载器 | BepInEx 5.4.23.6 |
| Harmony | 使用上述加载器提供的 HarmonyX 2.9.0 |
| 插件目标框架 | .NET Standard 2.1 |
| 本地构建工具 | .NET SDK 8、PowerShell |

启动时检查游戏主程序集 SHA-256，不匹配则停用补丁。当前支持值：

```text
de282e73a6e4b53a60a84e53ad30699a1fc5e9e90d0157e8a8eb37c6e7c81fe3
```

该保护不等于兼容性保证。游戏更新、其他 Mod 的补丁顺序、界面预制体变更，都可能影响运行。不要只修改哈希来绕过检查；适配新版本需要重新核对实际 API、字段、布局及补丁边界。

## 项目架构

```text
游戏原生配方／技能进度 ── GameRecipes ── RecipeStage
游戏背包／仓库状态     ── StockReader ── StockCounts
                                      │
                     Plugin 持有选择与周期性快照
                                      │
                  DemandEngine 需求汇总／共享分配
                                      │
           ┌──────────────────────────┼──────────────────────┐
        原生物品说明              Mission 心愿列表       拾取通知与角标
           │                          │                      │
           └──────── Harmony 补丁 + 原生 UI 附加组件 ────────┘
```

| 模块 | 职责 |
| --- | --- |
| [Plugin](<src/Plugin.cs>) | BepInEx 入口、程序集版本保护、独立配置／收藏持久化、约0.3秒周期性刷新、显示文本及生命周期清理。 |
| [GameRecipes](<src/GameRecipes.cs>) | 从运行中的开发、强化、技能数据库读取阶段和完成进度，建立稳定目标键，不分发离线游戏数据表。 |
| [DemandEngine](<src/DemandEngine.cs>) | 不依赖 Unity 的需求汇总、库存一次性分配、缺口及盈余计算；区分有限阶段和重复制作。 |
| [StockReader](<src/StockReader.cs>) | 读取真实持有量，处理格子来源、仓库、手持与临时区；不能确认的库存不假装已知。 |
| [NativeTooltips](<src/NativeTooltips.cs>) | 在原生说明中追加文本，管理长内容滚动，并在复用／关闭／停用时恢复原文本与几何状态。 |
| [NativeStoragePopups](<src/NativeStoragePopups.cs>)、[NativeWorkshopSalePopup](<src/NativeWorkshopSalePopup.cs>) | 跟踪仓库和售出界面的真实选择，复用完整原生弹窗；实际售出列表与旧格子售卖菜单分开处理。 |
| [NativeMissionWishlist](<src/NativeMissionWishlist.cs>) | 向原生 Mission 视图追加 UI 层心愿条目；临时 ID 不注册到 QuestManager 或游戏存档。保留真实任务的选择、追踪和奖励路径。 |
| [NativeMissionRowMark](<src/NativeMissionRowMark.cs>)、[NativeMissionMaterialMarks](<src/NativeMissionMaterialMarks.cs>)、[NativeMissionGuide](<src/NativeMissionGuide.cs>) | 左侧整体状态、右侧逐项图标、底部操作提示；矢量图标不依赖特殊字符字形。 |
| [NativeWishlistUI](<src/NativeWishlistUI.cs>)、[NativeWishlistFonts](<src/NativeWishlistFonts.cs>) | 开发／技能／物品角标、原生操作提示和仅作用于模组文字的字体后备处理。 |
| [NativePickupNotifications](<src/NativePickupNotifications.cs>) | 观察原生获取事务和物品日志，对比获取前后库存，避免把读档、单纯转存或持续满足误当成新达成。 |
| [NativeSettings](<src/NativeSettings.cs>)、[UiHooks](<src/UiHooks.cs>) | 原生 Mod配置分类、启用确认、设置应用；制作或技能学习后的收藏状态刷新。 |

### 实现边界

- 修改的是**运行时界面和方法行为**，不会在磁盘上改写原游戏 DLL。
- 公开版本不主动发放材料、修改库存、自动开发或加点。
- 可变原生文本／布局尽量采用保存与恢复方式；关闭功能、离开界面和卸载组件时释放自有 UI。
- Mission 的补丁检查预期的方法边界；不符合时不插入临时条目，而不是把伪任务交给真实任务逻辑。
- 这不是通用 Mod 框架：反射字段和 Harmony 补丁与目标游戏版本紧密耦合。

## 从源码构建

需要合法的本地游戏安装，以及 BepInEx 的核心引用。**不要把游戏程序集提交到 GitHub。**

推荐从仓库根目录执行 [构建脚本](<scripts/Build.ps1>)：

```powershell
# 仅编译
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 `
  -GamePath 'D:\Games\ANOMALITH'

# 编译并生成仅含本插件和文档的发布包
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 `
  -GamePath 'D:\Games\ANOMALITH' -Package

# 若 BepInEx 尚未放进游戏目录，可以指定已经解压的官方 core 目录
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 `
  -GamePath 'D:\Games\ANOMALITH' -BepInExCoreDir 'D:\Dependencies\BepInEx\core'
```

默认输出到 `artifacts/plugin/`，发布包位于 `artifacts/releases/`；可用 `-ArtifactsPath` 指定其他构建输出目录。脚本只编译／打包，不启动游戏、不自动安装，也不运行测试。

也可直接构建 [项目文件](<src/AnomalithWishlist.csproj>)：

```powershell
dotnet build .\src\AnomalithWishlist.csproj -c Release `
  -p:GameDir='D:\Games\ANOMALITH'
```

`GameDir` 也可通过环境变量 `ANOMALITH_GAME_DIR` 指定。编译引用全部标记为不复制到输出；打包使用明确白名单，只收录本插件 DLL 和文档。

仓库不包含验收／测试文件，编译通过不代表游戏内所有界面、分辨率、语言和手柄组合都得到验证。二次修改后的实机确认需自行完成。

## 风险与限制

1. **先备份存档。** 非官方 Mod 可能因版本、补丁冲突或程序错误导致异常；没有无损或兼容性担保。
2. **材料齐全不等于可以完成目标。** OV、研究等级、剧情及其他前置条件可能仍未满足。
3. **“全部配方”仅指当前读取并支持的配方范围。** 未收录系统、任务、兑换，以及未来重复制作不在有限总量承诺内。
4. **不能把“多余”作为无条件出售依据。** 统计范围和实际未来用途不同，错误出售无法由本 Mod 自动撤回。
5. **收藏跨存档共享。** 使用不同存档时清单可能相同，需求和完成情况则会变化；已完成记录的保留不是新的材料需求。
6. **UI 与输入仍有兼容风险。** 缩放、语言、其他界面 Mod、手柄映射可能造成错位、缺字或按键冲突。极窄拾取通知优先保留原文，可能不显示额外标识。
7. **不保证所有声明配方可达。** 游戏数据可能含锁定、特殊等级或未正常开放的阶段。
8. **不提供更新承诺。** 游戏升级后的兼容性由使用者／Fork 维护者自行处理；同时请遵守游戏及平台的使用条款。

## 公开范围与许可

本仓库只保留当前有效的生产源码、构建脚本及文档。旧独立 Tab 实现、验收清单、测试工程、测试打包脚本、补料作弊、自动诊断导出、反编译工具和调查数据均不在公开版本中。

游戏程序、第三方加载器、游戏素材／提取表、存档、用户配置、日志、本地备份和历史安装包也不随源码分发。忽略规则见 [.gitignore](<.gitignore>)，第三方权利与依赖来源见 [第三方声明](<THIRD-PARTY-NOTICES.md>)。

欢迎自由取用和自行维护。**不再新增功能；有需要就 Fork。** 本项目自身代码依 [MIT](<LICENSE>) 提供，游戏与第三方组件的权利不因此改变。
