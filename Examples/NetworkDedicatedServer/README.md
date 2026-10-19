# Network Dedicated Server

This example is a project Runtime Module plus a headless Server Target. The
module owns the network Schema, the `sample.set-position` command and the two
player authority policy. The engine Server owns the UDP transport, handshake,
fixed tick, snapshots and connection cleanup.

The example manifest explicitly enables `Editor`, `Game` and `Server`. Ordinary
projects only generate `Editor` and `Game`; enable the Server Target with the
Project Selector when a project needs a dedicated server.

Build the Server Target from the engine root:

```powershell
& .\Tools\SharpEngineProject.ps1 -Command Build `
  -ProjectFile .\Examples\NetworkDedicatedServer\NetworkDedicatedServer.sharpproject `
  -EngineRoot . -Target Server -Configuration Debug -Platform Win64
```

The native executable is emitted at:

```text
Binaries/Win64/Debug/Server/DedicatedServer.exe
```

Run it with the example project manifest and a bounded duration:

```powershell
& .\Binaries\Win64\Debug\Server\DedicatedServer.exe `
  --project .\Examples\NetworkDedicatedServer\NetworkDedicatedServer.sharpproject `
  --network-port 28760 --duration-milliseconds 10000
```

The saved World contains `player-a` and `player-b`. A client must complete the
Schema handshake before sending `sample.set-position`; the Server applies the
command on the World thread and emits snapshot or delta state after the
command result. Use `Tools/NetworkValidation` for the independent two-client
process test and duplicate MessageId check.
