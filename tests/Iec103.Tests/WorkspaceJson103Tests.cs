using Iec103;

namespace Iec103.Tests;

public class WorkspaceJson103Tests
{
    [Fact]
    public void 测试数据可加载并保留FUN_INF点位()
    {
        var workspace = WorkspaceJson103.Load(
            Path.Combine(AppContext.BaseDirectory, "TestData", "iec103-station.json"));

        Assert.Equal(2, workspace.Stations.Count);
        var station = workspace.Stations[0];
        Assert.Equal(1, station.Address);
        Assert.Equal(6, station.Points.Count);
        Assert.Equal((160, 1, 2), (station.Points[0].Fun, station.Points[0].Inf, station.Points[0].Dpi));
    }

    [Fact]
    public void 序列化往返保留全部字段()
    {
        var workspace = new Workspace103Model
        {
            Stations =
            [
                new Iec103StationModel
                {
                    Address = 7,
                    Name = "装置",
                    CompatIdent = "A",
                    PrivateIdent = "B",
                    Points = [new Iec103PointModel { Fun = 200, Inf = 9, Dpi = 2, Description = "点" }],
                },
            ],
        };

        var json = WorkspaceJson103.Save(workspace);
        Assert.NotEmpty(json);

        // 往返：写临时文件再加载
        var temp = Path.GetTempFileName();
        File.WriteAllText(temp, json);
        var loaded = WorkspaceJson103.Load(temp);
        File.Delete(temp);

        var source = workspace.Stations[0];
        var target = loaded.Stations[0];
        Assert.Equal(source.Address, target.Address);
        Assert.Equal(source.Name, target.Name);
        Assert.Equal(source.CompatIdent, target.CompatIdent);
        Assert.Equal(source.PrivateIdent, target.PrivateIdent);
        Assert.Equal(source.Points[0].Fun, target.Points[0].Fun);
        Assert.Equal(source.Points[0].Inf, target.Points[0].Inf);
        Assert.Equal(source.Points[0].Dpi, target.Points[0].Dpi);
    }

    [Fact]
    public void 地址或FUN_INF越界时拒绝加载()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceJson103.Save(new Workspace103Model
        {
            Stations = [new Iec103StationModel { Address = 0 }],
        }));

        Assert.Throws<InvalidOperationException>(() => WorkspaceJson103.Save(new Workspace103Model
        {
            Stations = [new Iec103StationModel { Address = 1, Points = [new Iec103PointModel { Fun = 256 }] }],
        }));
    }

    [Fact]
    public void 无传输字段的旧工作区默认tcp()
    {
        // 旧版工作区 JSON 不含 Transport 节点，或手工编辑显式写 null：均归一化为缺省 tcp
        foreach (var json in """
            { "Port": 2405, "Stations": [ { "Address": 1, "Name": "旧站" } ] }
            ---
            { "Transport": null, "Stations": [ { "Address": 1, "Name": "旧站" } ] }
            """.Split("---"))
        {
            var temp = Path.GetTempFileName();
            File.WriteAllText(temp, json);
            var loaded = WorkspaceJson103.Load(temp);
            File.Delete(temp);

            Assert.Equal("tcp", loaded.Transport.Type);
            Assert.Equal(9600, loaded.Transport.BaudRate);
            Assert.Equal("even", loaded.Transport.Parity);
        }
    }

    [Fact]
    public void 串口传输配置序列化往返保留()
    {
        var workspace = new Workspace103Model
        {
            Transport = new Transport103Model
            {
                Type = "serial", PortName = "COM3", BaudRate = 19200, Parity = "odd", DataBits = 8, StopBits = 2,
            },
            Stations = [new Iec103StationModel { Address = 1, Name = "串口站" }],
        };

        var json = WorkspaceJson103.Save(workspace);
        Assert.Contains("\"serial\"", json);
        Assert.Contains("COM3", json);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var loaded = WorkspaceJson103.Load(stream);
        Assert.Equal("serial", loaded.Transport.Type);
        Assert.Equal("COM3", loaded.Transport.PortName);
        Assert.Equal(19200, loaded.Transport.BaudRate);
        Assert.Equal("odd", loaded.Transport.Parity);
        Assert.Equal(8, loaded.Transport.DataBits);
        Assert.Equal(2, loaded.Transport.StopBits);
    }
}
