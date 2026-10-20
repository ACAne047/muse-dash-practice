# 原生分析资料

此目录整理了开发练习模组时生成的符号、字段、材质参数和目标方法 RVA/Length，以及一个便携分析脚本。不附带 GameAssembly、游戏程序集、第三方反编译源码或 Python 包。

安装 Python 3 和本工具的依赖后，在仓库根目录执行：

```powershell
python -m pip install -r .\tools\analysis\requirements.txt
python .\tools\analysis\disasm.py --game-root 'D:\SteamLibrary\steamapps\common\Muse Dash' --match 'PlayLongPressEffect'
python .\tools\analysis\disasm.py --game-root 'D:\SteamLibrary\steamapps\common\Muse Dash' --targets .\tools\analysis\metadata\fever-native-targets.json
```

默认分析 `character-native-targets.json`，输出至忽略的 `artifacts/analysis`。`--targets` 可换为 Fever、计时或 `animation-clock-native-targets.json` 目标；`--match` 只筛选已有精确长度目标，不按相邻符号距离猜测方法边界。可用环境变量 `MUSEDASH_GAME_ROOT` 代替 `--game-root`。

脚本先核对 `maintenance-state.json` 的 GameAssembly 指纹。现存地址仅适用于记录的本机游戏构建；更新游戏后需重新从 Cpp2IL dummy 元数据提取 RVA/Length，复核代码逻辑后才能更新基线。

`native-symbols.json.gz` 是原本地符号表的压缩副本，供 call/jmp 目标注释；字段及 shader JSON 为开发分析资料，不在运行时加载。角色／Fever 目标来自已保存的 Cpp2IL 方法属性，计时目标来自原分析脚本中记录的地址和长度。动画时钟目标来自本机 GlobalTimer／Spine Cpp2IL 方法属性，覆盖独立 Stopwatch 的创建、取值及 Spine 两个 Update 重载。
