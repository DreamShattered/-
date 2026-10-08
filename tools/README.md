# tools

开发期用于侦察与分析的独立脚本，不参与程序构建，运行时也不需要它们。

| 脚本 | 用途 |
|---|---|
| `pe_probe.py` | 解析 PE 头与导入表，识别目标程序使用的图形/输入 API（D3D8/9/11、DXGI、DInput 等） |
| `win_scan.py` | 枚举全部可见顶层窗口：进程、类名、尺寸、位置、置顶/穿透等样式标志 |
| `game_probe11.py` | 游戏现场勘察：启动目标、记录窗口与刷新率变化、用置顶探针窗口与像素采样判断是否为独占全屏、挂起前后画面对比 |

用法：
```bash
python tools/pe_probe.py path/to/target.exe
python tools/win_scan.py
python tools/game_probe11.py
```

`make_icon.py` 用于生成 `src/app.ico`（多尺寸图标，纯标准库手工编码 PNG/ICO，无第三方依赖）。