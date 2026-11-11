# 第三方依赖与权利说明

本仓库只发布 ANOMALITH Wishlist 插件自身的源代码和构建脚本，不包含第三方运行时二进制、游戏程序集、反编译游戏源码、游戏素材、存档或提取的数据表。

[MIT 许可证](<LICENSE>)只适用于本项目自身代码，不改变下列项目的许可，也不授予 ANOMALITH、Unity 或其他第三方资产的权利。

| 依赖 | 用途 | 来源及许可 |
| --- | --- | --- |
| BepInEx 5.4.23.6 | Mono 插件加载、配置与日志 | [官方发布](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6)，[MIT](https://github.com/BepInEx/BepInEx/blob/v5.4.23.6/LICENSE) |
| HarmonyX 2.9.0（由上述 BepInEx 提供） | 运行时方法补丁 | [上游](https://github.com/BepInEx/HarmonyX/tree/v2.9.0)，[MIT](https://github.com/BepInEx/HarmonyX/blob/v2.9.0/LICENSE)；包含[原始 Harmony 许可](https://github.com/BepInEx/HarmonyX/blob/v2.9.0/LICENSE.Harmony) |
| Newtonsoft.Json | 独立心愿记录序列化 | [上游及 MIT 许可](https://github.com/JamesNK/Newtonsoft.Json/blob/master/LICENSE.md)；编译时引用用户游戏安装中的版本，不随本项目分发 |
| Unity、TextMeshPro、Input System、Unity UI | 游戏的原生界面、输入与渲染 API | 来自用户合法安装的游戏；遵循各自原许可，不纳入本项目 MIT 授权 |
| ANOMALITH 游戏程序集与数据 | 读取配方、库存、进度和原生 UI 接口 | 由相关权利人保留权利；仅作本地编译／运行依赖，不分发 |

本项目与游戏开发者、发行商及上述项目没有官方合作、认可或从属关系。游戏名称仅用于说明兼容目标。

构建脚本的发布包只包含本插件 DLL 与本项目文档，不包含 BepInEx 加载器。若你自行合包分发第三方依赖，应自行保留其许可证并履行相应义务；例如 BepInEx 的加载链还涉及 Unity Doorstop、MonoMod、Mono.Cecil 等独立项目，不能以本项目的 MIT 许可代替这些项目的许可。
