using DiskEye.Etw;

namespace DiskEye.Tests;

public class SmokeTest
{
    [Fact]
    public void Constants_AreMeasuredValues()
    {
        Assert.Equal(0x20ul, KernelFileConstants.FileIoKeyword);
        Assert.Equal(12, KernelFileConstants.EventIdCreate);
        Assert.Equal(16, KernelFileConstants.EventIdWrite);
        Assert.Equal(36, KernelFileConstants.OffsetWriteSize);
        Assert.Equal(32, KernelFileConstants.OffsetPath);
        Assert.Equal(Guid.Parse("{EDD08927-9CC4-4E65-B970-C2560FB5C289}"),
                     KernelFileConstants.KernelFileProvider);
    }
}
