namespace Emotion.Signal;

/// <summary>Lecteur WAV minimal : PCM 16 ou 24 bits, replie en mono.</summary>
public static class Wav
{
    public static (float[] Samples, int Rate) ReadMono(string path, double startS, double lengthS)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);

        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("pas un RIFF");
        r.ReadUInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("pas un WAVE");

        int channels = 0, rate = 0, bits = 0;
        long dataOffset = 0, dataSize = 0;

        while (fs.Position + 8 <= fs.Length)
        {
            var id = new string(r.ReadChars(4));
            var size = r.ReadUInt32();
            var next = fs.Position + size + (size % 2);

            if (id == "fmt ")
            {
                r.ReadUInt16();
                channels = r.ReadUInt16();
                rate = (int)r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt16();
                bits = r.ReadUInt16();
            }
            else if (id == "data")
            {
                dataOffset = fs.Position;
                dataSize = size;
                break;
            }

            fs.Position = next;
        }

        if (bits != 16 && bits != 24) throw new NotSupportedException($"{bits} bits non gere");

        var bytesPerSample = bits / 8;
        var frameBytes = channels * bytesPerSample;
        var totalFrames = dataSize / frameBytes;
        var from = Math.Min((long)(startS * rate), totalFrames);
        var count = (long)Math.Min(lengthS * rate, totalFrames - from);

        fs.Position = dataOffset + from * frameBytes;
        var raw = r.ReadBytes((int)(count * frameBytes));

        var mono = new float[count];
        for (long i = 0; i < count; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                var o = (int)(i * frameBytes + c * bytesPerSample);
                sum += bits == 16
                    ? BitConverter.ToInt16(raw, o) / 32768f
                    // 24 bits petit-boutiste signe : on reconstitue le mot puis on etend
                    // le bit de signe depuis le rang 23.
                    : ((raw[o] | (raw[o + 1] << 8) | ((sbyte)raw[o + 2] << 16))) / 8388608f;
            }
            mono[i] = sum / channels;
        }

        return (mono, rate);
    }
}
