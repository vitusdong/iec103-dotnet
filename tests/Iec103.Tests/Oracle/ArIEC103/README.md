# Oracle：ArIEC103 从站模拟器（第三方源码 vendored）

来源：https://github.com/masarray/ARIEC103 （main 分支，Apache-2.0，见 LICENSE）

仅内部测试使用：作为 IEC 60870-5-103 的**独立第三方实现**，通过
`Iec103SlaveSimulatorSession` 与我们的 `Iec103Client`（主站）做互操作对拍，
校验两套 FT1.2/ASDU 栈的一致性。此目录文件保持上游原样（命名空间不变），
仅裁剪出从站会话所需的最小闭包（Core 解析/模型 + Master 协议构造器 +
Slave 模拟器 + 传输接口）；串口传输被替换为 `TcpServerByteTransport`
（`Oracle/TcpServerByteTransport.cs`，本仓库自有代码）。

上游文件头部均保留原始版权与 SPDX 标识。
