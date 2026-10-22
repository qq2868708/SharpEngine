# 网络、属性同步与 Dedicated Server 基础

状态：C# UDP 传输闭环已实现；QUIC、生产认证和客户端预测仍未纳入本阶段。

## 设计边界

网络模块位于 `Source/SharpEngine.Networking`，只依赖 `SharpEngine.Runtime`。它不修改 `RuntimeWorld` 的组件对象模型，也不让网络线程直接进入 World。Dedicated Server 使用现有 `RuntimeInstance` 和 `RuntimeWorld`，通过固定 Tick、提交边界和 `RuntimeFrame` 发布权威状态。

客户端发送已注册的 Command，服务器在 World 线程重新验证后执行。客户端不能通过网络直接写组件字段。Command 使用 `ConnectionId + MessageId` 做幂等键，重复消息返回缓存结果，不重复执行副作用。

## Schema

`NetworkSchemaCatalog` 登记两类稳定契约：

- `NetworkComponentDescriptor` 登记可复制组件的 `typeId`、schema 版本和字段；
- `NetworkRpcDescriptor` 登记 Command/Query/Event 的方法 ID、消息类别和可靠性。

`NetworkPropertyDescriptor.PropertyId` 是网络字段身份，`FieldName` 是 Runtime 状态中的字段名。两者可以在未来由 Source Generator 产生，网络包不依赖 CLR 类型名或字段顺序。Catalog 的确定性 JSON 通过 SHA-256 生成 `Fingerprint()`，客户端和服务器握手时应比较该指纹。

属性同步使用 `NetworkReplicationFrameBuilder`：没有基线时生成完整快照；带有 `NetworkReplicationBaseline` 时生成 `spawned`、`destroyed` 和字段级 `updated` 差分。快照从不可变 `RuntimeFrame` 读取，包含 `revision` 和 `baselineRevision`，因此不会暴露 Tick 中间状态。

相关性筛选在生成快照前执行。实体离开某个连接的相关集合时发送销毁；重新进入时发送完整实体。属性的 authority、可靠性和条件已经进入 Schema，实际网络传输层可以据此分配通道和预算。

## Dedicated Server

`DedicatedServerSession` 是一个无传输依赖的权威宿主：

1. 创建一个 `RuntimeInstance` 和一个 `RuntimeWorld`；
2. 在每个 Tick 开始前按输入序号处理排队 Command；
3. 调用现有 Runtime Tick；
4. 返回包含服务器 Tick、RuntimeFrame 和 Command 结果的 `NetworkServerFrame`；
5. 使用 `BuildSnapshot` 为每个连接生成完整快照或 Delta。

需要模块注册的宿主先使用 `DedicatedServerSession(Schema, Assets, Engine)` 创建 Instance，
再注册子系统、启动模块，最后调用 `StartWorld(Scene, Registry)`。启动前 `ActiveWorld` 为 null；
`World` 在尚未启动时报告 `SERVER_WORLD_NOT_STARTED`。原有带 Scene 的构造函数仍可用于已经准备好注册的独立会话。
Server Target 遵守这一顺序，组件初始化能够查询模块的 WorldSubsystem。

未提供 Engine 的独立 Session 在 Tick 中推进自己拥有的 EngineLifetime，再推进 Instance/World。
显式提供 Engine 时，调用者每帧推进该共享 Engine 一次，Session 不重复推进它。
Server Target 关闭时先释放网络、World/Instance，再卸载模块，最后释放 Engine。
`StartWorld` 只允许成功启动一次；失败不会留下部分 World。

它不启动渲染器或编辑器 UI，也不把 Runtime 状态写回 Authoring 资产。`DedicatedServerNetworkHost` 将 UDP 连接映射到该会话，不能绕过 CommandRouter 和 World 提交边界。

## C# UDP 传输

`UdpNetworkTransport` 基于 C# LiteNetLib UDP 栈，提供连接管理、MTU 分片、CRC32C、连接超时和四种投递方式：可靠有序、可靠时序、不可靠时序和不可靠。每条消息仍使用 `SEN1` wire header 加版本化 JSON；payload 大小、可靠队列和事件队列都有上限，超过预算会返回结构化错误或断开连接。

`DedicatedServerNetworkHost` 要求连接先比较 Schema SHA-256 指纹，再接收 Command。网络回调只入队，World 线程在下一次权威 Tick 前检查权限并执行。每个连接的快照基线只在收到应用层 ACK 后推进。`NetworkClientSession` 将匹配 baseline、单调 Tick 和已注册属性的快照原子提交到 `NetworkReplicaState`。

## 示例项目与 Server Target

`Examples/NetworkDedicatedServer` 展示项目 Runtime Module 如何通过
`INetworkServerRuntime` 注册组件属性、Command 和权限策略。示例 World 包含
`player-a` 与 `player-b` 两个 Transform 实体；模块只在 Server Target 中引用
`SharpEngine.Networking`，Editor/Game 不加载这套 DS 代码。

Server Target 是项目级可选能力。没有 `targets` 字段的旧项目默认生成
`Editor` 和 `Game`。需要 DS 时，在 Windows 资源管理器中右键项目的
`.sharpproject` 文件，选择 **Add Server Target**；Windows 11 的简化菜单中
可先选择“显示更多选项”。该操作会更新项目 manifest 的 `targets` 数组，
自动重新生成 solution、Server Target wrapper 和启动配置。Rider 重新加载
solution 后即可看到 `BuildTarget/Server Target` 和 `Debug Server` 配置。

同一操作也可通过命令行调用：

```powershell
& .\Tools\SharpEngineProject.ps1 -Command AddTarget -Target Server `
  -ProjectFile .\Sandbox.sharpproject -EngineRoot .
```

右键 **Remove Server Target** 或运行 `RemoveTarget -Target Server` 会移除
manifest 中的 Server Target，并清理对应的生成文件和启动配置。

使用统一项目构建入口生成 Server 模块并构建原生目标：

```powershell
& .\Tools\SharpEngineProject.ps1 -Command Build `
  -ProjectFile .\Examples\NetworkDedicatedServer\NetworkDedicatedServer.sharpproject `
  -EngineRoot . -Target Server -Configuration Debug -Platform Win64
```

构建产物为 `Binaries/Win64/Debug/Server/DedicatedServer.exe`。它只加载
Runtime Module、Runtime 和 Networking 依赖，不创建窗口、编辑器或渲染器。
可以使用 `Tools/NetworkValidation/RunDedicatedServerValidation.ps1` 启动 DS 和
两个独立客户端，验证 Schema 握手、重复 MessageId 幂等、两个玩家位置同步以及
Server 退出清理。

最小启动方式：

```csharp
using var Transport = new UdpNetworkTransport(new NetworkTransportOptions { LocalPort = 27015 });
using var Host = new DedicatedServerNetworkHost(Session, Transport);
while (Running)
{
    Host.Tick(RuntimeWorld.FixedDeltaSeconds);
}
```

客户端使用同一个 `NetworkSchemaCatalog` 创建 `NetworkClientSession`，调用 `Connect`、周期性 `Poll`，握手完成后调用 `SendCommand`。Schema 不匹配会在进入命令阶段前断开。

## 验证

运行：

```powershell
dotnet run --project Tools/NetworkValidation/SharpEngine.NetworkValidation.csproj
```

该验证覆盖 Schema 指纹、完整快照、字段 Delta、Server Tick、重复 Command 的幂等结果、未知 Command 拒绝、相关性过滤，以及两个独立 UDP transport 实例之间的握手、快照 ACK、Command 和属性回传。示例 DS 脚本另外覆盖原生 Server Target 的模块加载和双客户端运行。它不代表 QUIC、生产认证、客户端预测、客户端渲染或生产规模 DS 验收。
