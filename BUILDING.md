# Building

## Requirements

- Windows 64 位
- .NET SDK 8 或更高版本
- Git Bash 或其他 POSIX shell，打包脚本需要
- BepInEx 5.4.23.5
- 游戏本体，构建时需要它的 Managed DLL

## 构建插件

1. 复制 `GameRoot.props.example` 为 `GameRoot.props`，填写游戏安装目录。目录结尾保留反斜杠。
2. 运行：

```bash
dotnet build -c Release
```

也可以不创建 `GameRoot.props`，直接传参数：

```bash
dotnet build -c Release -p:GameRoot="C:\Program Files (x86)\Steam\steamapps\common\再再启程 One Way Tickets\"
```

输出文件是 `bin/Release/netstandard2.1/OWT.AutoRepair.dll`。默认会把 DLL 复制到游戏的 `BepInEx/plugins`；不需要时加 `-p:DeployToGame=false`。

## 构建发布包

```bash
GAME_ROOT="C:/Program Files (x86)/Steam/steamapps/common/再再启程 One Way Tickets" \
  bash packaging/build-release.sh
```

脚本会在 `_cache` 下查找 BepInEx 压缩包，没有就下载，然后编译插件、组装游戏根目录结构、生成 zip 和 `SHA256SUMS.txt`。

可用环境变量：

- `GAME_ROOT`：游戏安装目录，必填
- `BEPINEX_ZIP`：BepInEx 压缩包路径，默认 `_cache/BepInEx_win_x64_5.4.23.5.zip`
- `DIST_DIR`：发布包输出目录，默认 `dist`
- `CACHE_DIR`：缓存目录，默认 `_cache`

发布包包含 BepInEx 与插件，还有配置、安装说明和第三方许可证。不会包含游戏 DLL 或游戏素材。

## CI

干净的 GitHub runner 没有游戏 DLL，无法直接构建。请本地发布，或者使用 self-hosted runner。不要把游戏 DLL 提交到仓库里。
