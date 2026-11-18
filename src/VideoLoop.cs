using System;
using System.IO;

internal sealed class VideoLoop : IDisposable
{
    private readonly FileStream input;
    private readonly int chunkSize;
    private bool waitingForCapacity;

    internal VideoLoop(Panel panel, string path)
    {
        input = File.OpenRead(path);
        try
        {
            if (input.ReadByte() != 0 || input.ReadByte() != 0)
                throw new InvalidDataException("Select an Annex B H.264 elementary stream.");
            int marker = input.ReadByte();
            if (marker == 0) marker = input.ReadByte();
            if (marker != 1 || input.Length < 8)
                throw new InvalidDataException("Select an Annex B H.264 elementary stream.");
            input.Position = 0;
            chunkSize = panel.StartVideo();
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    internal void Advance(Panel panel)
    {
        if (waitingForCapacity && !panel.HasVideoCapacity()) return;
        byte[] data = new byte[(int)Math.Min(input.Length - input.Position, chunkSize)];
        int offset = 0;
        while (offset < data.Length)
        {
            int count = input.Read(data, offset, data.Length - offset);
            if (count == 0) throw new EndOfStreamException("The background video ended unexpectedly.");
            offset += count;
        }
        bool last = input.Position == input.Length;
        waitingForCapacity = panel.SendVideoChunk(data, last);
        if (last) input.Position = 0;
    }

    public void Dispose() { input.Dispose(); }
}
