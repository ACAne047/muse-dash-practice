# Muse Dash A–B 练习模组

支持官谱和自制谱的分段循环练习模组。当前版本 **0.3.7**，基于 MelonLoader；优先在场景内回跳，必要时回退为重新载入谱面。

## 安装

已核对环境：Windows x64、Muse Dash 6.7.0、Unity 2019.4.41f1、MelonLoader 0.7.0。现成 DLL 位于 `releases/0.3.7`，直接安装无需编译。

关闭游戏，在仓库根目录打开 PowerShell，执行以下命令，将路径替换为自己的游戏目录：

```powershell
.\scripts\Install.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Muse Dash'
```

脚本会校验游戏指纹和发布 DLL，替换前备份已有的练习 DLL。首次准备 MelonLoader、手动安装、禁用和回退步骤见 [安装说明](docs/安装说明.md)。

## 使用

打开面板点击 `Enable practice`，再进入谱面。初始 A=0、B 未设，等待选段。

| 操作 | 功能 |
| --- | --- |
| F6 | 显示／隐藏面板 |
| F7 | 设置 A，清除旧 B，停止旧循环并继续播放选段 |
| F8 | 设置 B，区间有效时自动开始循环 |
| F9 | 开始／重新开始；缺少或无效参数时暂停游戏 |
| 面板 A / B / Lead-in | 手动填写秒数；提前时间默认 3 秒，可设为 0 或小数 |

练习启用期间禁用 Fever。启用练习后，本次游戏进程的成绩保存和上传持续阻断；恢复正常记分需完全退出并重启游戏。完整操作见 [使用说明](docs/使用说明.md)。

## 构建与检查

安装 .NET SDK 6，并让本机 MelonLoader 已生成 IL2CPP 代理程序集。项目不附带游戏程序集，NuGet 包源为空。

```powershell
.\scripts\Build.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Muse Dash'
.\scripts\Test.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Muse Dash'
```

构建 DLL 输出到 `artifacts/mod`。也可设置当前 PowerShell 会话的 `$env:MUSEDASH_GAME_ROOT`，省略各脚本的 `-GameRoot`。构建和检查脚本不会安装或启动游戏。

## 仓库内容

| 目录／文件 | 内容 |
| --- | --- |
| `src/` | 当前模组源码与项目 |
| `tests/` | 输入路由、区间控制回归、编译后静态审计和安装脚本隔离验证 |
| `scripts/` | 构建、检查、安装、禁用脚本 |
| `releases/0.3.7/` | 原发布 DLL、原始验证记录 |
| `docs/` | 安装、使用、维护说明 |
| `tools/analysis/` | 原生分析工具、符号及字段资料 |
| `CHANGELOG.md` | 精简的修复与更新记录 |
| `maintenance-state.json` | 版本、文件位置和 SHA-256 基线 |

0.2.1～0.3.7 的 9 个本地版本已按源码快照导入 Git，标签为 `v0.2.1`～`v0.3.7`。这些是本次迁移生成的归档提交，不是原始开发提交；提交身份使用 `Codex <codex@localhost>`，没有改动全局 Git 身份。`main` 在快照基础上整理目录和可移植脚本，运行时 C# 源码保持 0.3.7 原样。历史标签中的绝对路径和早期“官谱”表述属于旧文档，以当前说明为准。

仓库不含游戏程序集、个人日志、存档和旧备份；已有备份仍保留在原工作区。当前没有配置远程仓库。维护入口见 [维护记录](docs/维护记录.md)。
