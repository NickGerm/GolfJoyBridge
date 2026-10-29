namespace GolfJoyBridge;

/// <summary>Raw camera buffer as delivered by the unit's image callback (copied; safe to keep).</summary>
public sealed class RawImage
{
    public int Width { get; }
    public int Height { get; }
    public int Count { get; }
    public int BytesPerPixel { get; }
    public byte[] Data { get; }

    public RawImage(int width, int height, int count, int bytesPerPixel, byte[] data)
    {
        Width = width; Height = height; Count = count; BytesPerPixel = bytesPerPixel; Data = data;
    }
}

/// <summary>A shot's camera frames as 8-bit grey images with the brightness curve already applied.</summary>
public sealed class FrameSet
{
    public int Width { get; }
    public int Height { get; }
    public byte[][] Frames { get; }
    public int Count => Frames.Length;

    public FrameSet(int width, int height, byte[][] frames)
    {
        Width = width; Height = height; Frames = frames;
    }

    /// <summary>Builds grey frames from the raw buffer. Returns null if the pixel format is not supported.</summary>
    public static FrameSet? FromRaw(RawImage raw, double gamma)
    {
        if (raw.BytesPerPixel != 1)
        {
            Log.Info($"Camera frames are {raw.BytesPerPixel} bytes/pixel; only 8-bit grey is supported, so the shot picture is skipped.");
            return null;
        }

        long frameSize = (long)raw.Width * raw.Height;
        if (frameSize <= 0 || raw.Count <= 0 || raw.Data.Length < frameSize * raw.Count)
        {
            Log.Info($"Camera buffer is smaller than expected ({raw.Data.Length} bytes for {raw.Count} frames of {raw.Width}x{raw.Height}).");
            return null;
        }

        var lut = new byte[256];
        double g = Math.Clamp(gamma, 0.2, 3.0);
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp(Math.Round(255.0 * Math.Pow(i / 255.0, g)), 0, 255);

        var frames = new byte[raw.Count][];
        for (int f = 0; f < raw.Count; f++)
        {
            var frame = new byte[frameSize];
            long offset = f * frameSize;
            for (long i = 0; i < frameSize; i++) frame[i] = lut[raw.Data[offset + i]];
            frames[f] = frame;
        }
        return new FrameSet(raw.Width, raw.Height, frames);
    }
}
