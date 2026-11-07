# 编辑器性能与验收

## 性能验收

JSON 资产的 dirty 状态在文档变更时计算，未变化的源文本与 Source 预览复用派生缓存；
编辑、Undo/Redo 和 Save 后立即反映最新已提交内容，跨保存点仍按内容判断 dirty。
这些缓存不写入源文件，不改变资产版本或外部文件冲突检查。

性能是编辑器变更的验收条件。相同项目、资源、构建配置、后端、VSync、调试器和窗口条件下，
保留平均/P95 帧时间与真实导航响应；功能通过不能掩盖可复现的性能下降。
`Tools/Assets/ValidateEditorPerformance.py --record-baseline --output <目录>` 记录独立会话基线；
候选版本使用 `--baseline <基线目录/report.json> --output <新目录>` 对照，并保留完整 MCP 目录、
原始 timing、原生滚轮导航结果和匹配截图。默认目标为 Debug/OpenGL/VSync on，
平均帧时间不超过 18 ms、P95 不超过 25 ms；基线比较的 5% 只用于识别测量波动，
持续退化仍须定位修复。该工具仅启动与清理自己的测试会话，不替换用户编辑器。

使用 `--render-vsync off` 可测量取消显示同步等待后的帧时间；保持默认值则验证日常显示设置。
结果中的 `cpuMsPerFrame` 使用编辑器进程累计 CPU 时间与实际提交帧数计算，包含后台渲染/GC
线程的进程 CPU 开销，不包含等待显示同步的墙钟时间。对照相同设置和资源负载时，可用它
检查 60 FPS 下被同步等待掩盖的 CPU 回退；不能将主场景的 `sceneAverageMs` 当作整个编辑器的 CPU 成本。
