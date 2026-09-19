using DiskEye.Etw;

namespace DiskEye.Tests;

public class BoundedFrameWriterTests
{
    [Fact]
    public void OverCapacity_DropsAndCounts_NeverBlocks()
    {
        var sw = new StringWriter();
        using (var bfw = new BoundedFrameWriter(sw, capacity: 5, autoStart: false))
        {
            for (int i = 0; i < 100; i++) bfw.Enqueue($"L{i}");
            Assert.Equal(95, bfw.DroppedCount);
            Assert.Equal(5, bfw.QueueDepth);
            bfw.Start();
        }
        // 入队的 5 行全部排空写出
        var outLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, outLines.Length);
    }

    [Fact]
    public void UnderCapacity_DeliversAll()
    {
        var sw = new StringWriter();
        using (var bfw = new BoundedFrameWriter(sw, capacity: 100))
        {
            for (int i = 0; i < 10; i++) bfw.Enqueue($"X{i}");
        }
        var outLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(10, outLines.Length);
    }
}
