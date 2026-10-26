# Iec103.NET

[![CI](https://github.com/vitusdong/iec103-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/vitusdong/iec103-dotnet/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/github/license/vitusdong/iec103-dotnet)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)

[中文](#中文) | [English](#english)

自研的 **IEC 60870-5-103 / DL/T 667-1999** 继电保护设备通信协议栈（.NET 8，纯托管 C#），主站（Master）与从站（Slave）双向完整实现。

## 中文

### 为什么有这个库

IEC 60870-5-103（保护设备信息接口配套标准）在 .NET 生态里几乎空白：[lib60870.NET](https://github.com/mz-automation/lib60870) 只覆盖 101/104，[ArIEC103](https://github.com/masarray/ARIEC103) 是 Wireshark 解析器 + 模拟器而非协议库。本库从 FT1.2 链路层到应用层全部自写，并经第三方实现互操作对拍验证。

### 特性

- **FT1.2 链路层**：固定/可变帧长、字节转义、校验和、链路复位
- **ASDU 编解码**：FUN/INF 映射、COT 语义文本、CP32 时间、GB18030 描述编码（向下兼容 ASCII）
- **主站**：复位链路、时钟同步（带回读排空）、总召唤、1/2 级数据召唤、**通用分类目录读取 + 写值**、**扰动记录表 + 完整录波取回（通道化采样）**、一般命令、二级测量
- **从站**：TCP 多主站会话监听、动态置点、保护事件、扰动注入、运行时命令注入（`RuntimeCommands103`）
- **传输**：TCP（主/从）与串口（`System.IO.Ports`），也可注入任意 `Stream`
- **序列化**：点表/扰动/通用分类目录的 JSON 工作区导入导出（`WorkspaceJson103`）
- 零 GPL 依赖，MIT 许可，可直接商用

### 快速开始

```bash
dotnet build
dotnet test        # 93 项测试，含第三方互操作对拍
```

起一个从站并让主站总召唤：

```csharp
using Iec103;

// 从站：定义保护装置点表
var workspace = new Workspace103Model
{
    Stations =
    [
        new Iec103StationModel
        {
            Address = 1,
            CompatIdent = "TEST-103",
            Points = [ new() { Fun = 160, Inf = 1, Dpi = 2, Description = "断路器合位" } ],
        },
    ],
};

using var server = new Iec103Server(workspace, port: 2404);
server.Start();

// 主站：复位 → 时钟同步 → 总召唤
using var client = new Iec103Client("127.0.0.1", server.Port);
client.Connect();
client.ResetLink(address: 1);
client.SendClockSync(1, DateTime.Now);
var asdus = client.DrainClass1UntilGiEnd(1);   // 总召唤至 GI 终止帧

// 读保护装置的通用分类目录与扰动录波
var profile = client.ReadGenericProfile(1);
var list    = client.RequestDisturbanceList(1);
var wave    = client.ReadDisturbance(1, fan: list[0].Fan);  // 含通道采样
```

### 测试策略（93 项）

| 层次 | 内容 |
|------|------|
| 单元 | FT1.2 帧编解码、ASDU 各类型往返、GB18030 边界截断 |
| 自环 | TCP/串口内存管道承载，主从全流程对拍 |
| **互操作 oracle** | 与 vendored [ArIEC103](https://github.com/masarray/ARIEC103)（Apache-2.0）从站模拟器真实互操作 |
| 硬化 | 畸形帧、超长描述、非法参数等边界注入 |

### 目录结构

```
src/Iec103/          协议库（链路层 / ASDU / 主站 / 从站 / 序列化）
tests/Iec103.Tests/  xUnit 测试 + ArIEC103 互操作 oracle
docs/protocol-notes.md  协议调研笔记（类型/时序/对拍认知）
```

### 许可

- 库本体：**MIT**（见 [LICENSE](LICENSE)）
- `tests/Iec103.Tests/Oracle/ArIEC103/`：vendored 第三方代码，**Apache-2.0**，仅用于测试（见 [NOTICE](NOTICE)）

---

## English

A from-scratch **IEC 60870-5-103** (protective equipment companion standard, a.k.a. DL/T 667-1999) protocol stack for .NET 8 — full master **and** slave implementation in pure managed C#.

The .NET ecosystem has essentially no 103 implementation: lib60870.NET covers only 101/104, and ArIEC103 is a dissector/simulator rather than a library. This stack implements everything from the FT1.2 link layer up, and is verified by interop testing against a third-party implementation.

**Highlights**: FT1.2 framing (fixed/variable length, escaping, checksums) · full ASDU codec (FUN/INF, COT, CP32 time, GB18030 descriptors) · master commands incl. clock-sync with echo drain, general interrogation, **generic classification read/write**, **disturbance record retrieval with per-channel waveform samples** · TCP & serial transports, or inject any `Stream` · JSON workspace serialization · 93 tests incl. an ArIEC103 interop oracle · MIT, zero GPL dependencies.

```bash
dotnet build && dotnet test
```

### License

MIT for the library; vendored ArIEC103 test oracle under Apache-2.0 (see [NOTICE](NOTICE)).
