# OWT Auto Repair

《再再启程 One Way Tickets》的自动维修模组。进入旅途后自动处理装置维修和灭火，也会清洁装置、剪断藤蔓，并处理故障，可选唤醒昏迷角色。

## 功能

- 自动修好已损毁的装置
- 自动灭火
- 自动清理装置上的水滴和酸液
- 自动剪断缠绕装置的藤蔓
- 自动处理装置故障
- 自动唤醒昏迷角色，默认开启
- 可选回复未损毁装置的 HP，默认关闭
- 有对应工具时使用游戏内真实工具完成动作，没有工具时按 `Mode` 回退到后台处理
- 毛巾按最久未服务的机器轮转；其他机器也积水时，最多连续擦 1.2 秒就换台
- 左上角窗口显示当前状态，可以随时开关
- `ForceAutoRotate` 可让自动维修临时使用自动扳手的旋转逻辑

## 要求

- Windows 64 位
- Steam 版《再再启程 One Way Tickets》
- BepInEx 5.4.23.5，发布包已附带

## 安装

1. 退出游戏。
2. 从 Releases 下载最新的整合包。
3. 解压到游戏根目录，和 `再再启程.exe` 放在同一层。
4. 提示合并或覆盖时选择“是”。
5. 从 Steam 启动游戏。

已经装过 BepInEx 时，只需要覆盖 `BepInEx/plugins/OWT.AutoRepair.dll` 和 `BepInEx/config/owt.autorepair.cfg`。覆盖配置前先备份自己改过的 cfg。

## 使用

自动维修默认开启。进入世界后，左上角会出现 `OWT Auto Repair` 窗口，可以点击开关，也可以切换 `Force Auto Wrench`。

`Mode` 控制工具使用方式：

- `Hybrid`：有工具时用真实工具，没有工具时后台处理。
- `ToolsOnly`：只用真实工具，没有工具就不处理。
- `Background`：全部后台处理，不播放工具动作。

## 配置

配置文件：`BepInEx/config/owt.autorepair.cfg`。修改后重启游戏生效。

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `Enabled` | `true` | 总开关 |
| `Mode` | `Hybrid` | 工具使用方式 |
| `ForceAutoRotate` | `true` | 自动维修期间临时把当前扳手设为自动旋转 |
| `TowelDirectMode` | `false` | `false` 使用原本的毛巾擦拭动画；`true` 改为直接移除水滴 |
| `TowelSwapSeconds` | `1.2` | 其他机器也积水时，毛巾处理多久后换台 |
| `TowelStallSeconds` | `2` | 连续多久没有实际移除事件后触发兜底清理 |
| `Diagnostics` | `true` | 输出毛巾问题的详细诊断日志 |
| `ShowOnScreenButton` | `true` | 显示左上角开关窗口 |
| `RepairBrokenDevices` | `true` | 自动修好已损毁的装置 |
| `RepairDamagedDevices` | `false` | 自动回复未损毁装置的 HP |
| `ExtinguishFire` | `true` | 自动灭火 |
| `DryWetDevices` | `true` | 自动擦干水滴 |
| `CleanAcidDevices` | `true` | 自动清理酸液 |
| `CutVines` | `true` | 自动剪断藤蔓 |
| `FixStupor` | `true` | 自动处理装置故障 |
| `WakeComatoseCharacters` | `true` | 自动唤醒昏迷角色 |
| `LogRepairs` | `false` | 在日志里记录每次动作 |

更多参数和取值范围见配置文件里的注释。

## 毛巾调度

暴雨会让多台机器同时积水。插件记录每台机器上次被服务的时间，优先处理最久没处理的机器；如果另一台机器也在积水，当前毛巾最多连续擦 1.2 秒就换台。这样最后一台机器不会一直排不上。

毛巾处理水滴或酸液时会延迟 `DeviceWaterView.reinit` / `DeviceAcidView.reinit`，避免游戏在一次 raycast 循环中销毁后续目标。问题解决后视图会统一重建。

## 兼容性和注意事项

- 这个模组会自动完成原本需要手动操作的维修动作，可能影响游戏平衡。
- 插件用合成的鼠标位置驱动游戏工具。水滴重叠或 UI 遮挡会导致目标没命中；暴雨快速重建视图时也可能出现。超过 `TowelStallSeconds` 后，插件会直接移除一个水滴作为兜底。
- 一次只使用一个工具。暴雨里只有单目标毛巾时，清理速度可能跟不上所有机器；可以调低 `TowelSwapSeconds`，或者使用拖布、热风枪等范围工具。
- 科技树未解锁的工具不会被使用。扳手线升级后，插件会使用对应等级的扳手。
- 游戏更新可能改变 Harmony 补丁目标，导致模组失效。
- 调试前备份 `SaveData/save_00.dat`。
- `winhttp.dll` 可能被杀毒软件误报。不放心的话不要安装。
- 本项目与游戏开发商无关。

## 从源码构建

需要 .NET SDK 8 或更高版本。

1. 复制 `GameRoot.props.example` 为 `GameRoot.props`，把 `GameRoot` 改成你的游戏安装目录。目录结尾保留反斜杠。
2. 运行：

```bash
dotnet build -c Release
```

插件输出到 `bin/Release/netstandard2.1/OWT.AutoRepair.dll`。构建成功后默认会复制到 `BepInEx/plugins`；不需要时加 `-p:DeployToGame=false`。

也可以直接传路径：

```bash
dotnet build -c Release -p:GameRoot="C:\Program Files (x86)\Steam\steamapps\common\再再启程 One Way Tickets\"
```

## 卸载

删除 `BepInEx/plugins/OWT.AutoRepair.dll`。

如果不再使用任何模组，再删除游戏根目录的以下内容：

- `winhttp.dll`
- `doorstop_config.ini`
- `.doorstop_version`
- `changelog.txt`
- `BepInEx` 文件夹

## 许可证

MIT，见 [LICENSE](LICENSE)。

## 致谢

- BepInEx
- HarmonyX
- MonoMod
- Mono.Cecil

第三方许可证文本在 `packaging/LICENSES`。
