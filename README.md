# Codex 使用仪表盘

Windows 桌面上的 Codex 悬浮使用仪表盘。它读取 Codex 本地 `app-server` 的官方账户额度响应，与 Codex 客户端使用同一数据源，不统计 OpenAI API 用量，也不估算 token。

## 功能

- 显示 Codex 返回的全部额度窗口，包括剩余百分比、进度条和重置时间。
- 显示 `rateLimitResetCredits.availableCount`，即当前可用的手动额度重置机会数量。
- 显示最近活跃任务的当前模型、推理强度和上下文使用量。
- 检测 Codex Desktop、Codex CLI、IDE 中的 Codex 进程；任一运行时仪表盘自动出现，全部结束后自动隐藏。
- 仪表盘不强制置顶，可以被其他窗口正常覆盖。
- 点击右上角的图钉可以切换置顶；开启时图钉按钮会变为青绿色。
- 仪表盘窗口会在 Windows 任务栏中显示图标。
- 每 30 秒自动刷新，“已同步”旁会显示下一次刷新的秒数倒计时，也可以点击右上角刷新按钮。
- 额度低于 50%/20% 时，进度条和状态指示色会随之变化。
- 查询失败时明确显示“额度不可用”，不会显示估算结果。

## 使用

首次安装：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

这会在项目根目录直接生成 `CodexDashboard.exe`，创建桌面快捷方式，添加当前用户级的登录启动项，并以脱离 Codex 进程树的方式启动后台监控。窗口位置和诊断日志也保存在项目根目录，不再向 `LocalAppData` 复制程序。

仅在项目目录试运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\start.ps1
```

卸载：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1
```

## 操作

- 按住仪表盘空白处可以拖动。
- 点击右上角的 `↻` 立即刷新。
- 点击刷新按钮旁的 `—` 最小化到系统托盘；双击托盘图标可以恢复。
- 点击 `📌` 可让仪表盘始终显示在其他窗口上方，再点一次取消置顶。
- 双击桌面上的 `Codex Usage Dashboard`，或直接运行项目根目录的 `CodexDashboard.exe`，可以手动显示仪表盘。如果后台已经运行，它会唤醒现有实例。
- 右键仪表盘可刷新、重新显示或退出后台监控。
- 退出后台监控只影响本次登录；若已安装，下次登录时仍会自动启动。要永久关闭请运行卸载脚本。

## 数据与安全

程序只调用本机 `codex.exe app-server --stdio` 的只读 `account/rateLimits/read` 和 `thread/list` 方法，并读取最近活跃任务在 Codex 本地会话日志中的官方 token 计数事件。它不读取、复制或保存 Codex 登录凭据，也不会调用重置额度的方法。

如果 Codex 更新后本地协议发生不兼容，仪表盘会显示“额度不可用”。更新本项目后重新运行 `install.ps1` 即可覆盖安装。
