using System.Threading.Channels;
using DiskMon.Core.Data;

namespace DiskMon.Core.Sampling;

/// <summary>
/// In-process hand-off of Volume Samples from the VolumeSampler to Swing detection. Bounded and dropping the
/// oldest sample when full, so a slow or absent reader can never make the Collector grow without limit.
/// </summary>
public sealed class VolumeSampleChannel
{
    /// <summary>About 2 hours of samples for 4 Volumes at the default 30 s interval.</summary>
    public const int Capacity = 1024;

    private readonly Channel<VolumeSample> _channel = Channel.CreateBounded<VolumeSample>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });

    public ChannelReader<VolumeSample> Reader => _channel.Reader;
    public ChannelWriter<VolumeSample> Writer => _channel.Writer;
}
