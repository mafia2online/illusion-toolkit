using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Illusion.Formats.Compiled;

/// <summary>Authored Mafia III analytical wave templates. Dynamic ripples and reflection inputs
/// are separate runtime passes and are not described by this configuration.</summary>
public sealed class CompiledWaterWaves
{
    public IReadOnlyList<CompiledWaterWaveTemplate> Templates { get; }
    private CompiledWaterWaves(CompiledWaterWaveTemplate[] templates) => Templates = Array.AsReadOnly(templates);

    public static CompiledWaterWaves LoadConfiguration(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return ReadConfiguration(XDocument.Load(reader));
    }

    public static CompiledWaterWaves ReadConfiguration(XDocument document)
    {
        var nodes = document.Descendants("m_WaterParams").ToArray();
        if (nodes.Length != 1) throw new InvalidDataException("Expected exactly one native m_WaterParams configuration.");
        var templates = nodes[0].Element("m_WaveTemplates")?.Element("Items")?.Elements("S_WaterWaveTemplate").ToArray();
        if (templates?.Length != 4) throw new InvalidDataException("Native water configuration requires four wave templates.");
        var output = new CompiledWaterWaveTemplate[4];
        for (int i = 0; i < output.Length; i++)
        {
            var rows = templates[i].Element("m_Waves")?.Element("Items")?.Elements("S_WaterWaveParams").ToArray();
            if (rows?.Length != 4) throw new InvalidDataException("Each native wave template requires four waves.");
            float Read(XElement row, string name)
            {
                if (!float.TryParse(row.Element(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || !float.IsFinite(value)) throw new InvalidDataException("Missing or nonfinite water wave field " + name + ".");
                return value;
            }
            output[i] = new(Array.AsReadOnly(rows.Select(row => new CompiledWaterWave(
                Read(row, "m_Amplitude"), Read(row, "m_WaveLength"), Read(row, "m_Angle"),
                Read(row, "m_Speed"), Read(row, "m_Pinch"))).ToArray()));
        }
        var result = new CompiledWaterWaves(output);
        result.PackConstants(); // Reject finite inputs whose native products overflow.
        return result;
    }

    /// <summary>Native d008: four templates, each eight float4 rows, 512 bytes total.
    /// Row components correspond to the four waves. Angles are degrees; time is seconds.
    /// Phase uses world XY plus the engine floating-origin XY before these constants.</summary>
    public float[] PackConstants()
    {
        var output = new float[128];
        for (int template = 0; template < 4; template++)
            for (int wave = 0; wave < 4; wave++)
            {
                var w = Templates[template].Waves[wave];
                float inverse = w.WaveLength >= 1e-5f ? 1 / w.WaveLength : 100000;
                float radians = w.Angle * .01745329238474369f;
                float sin = MathF.Sin(radians), cos = MathF.Cos(radians);
                float half = (w.Pinch * w.Amplitude) * .5f;
                float x = half * sin, y = half * cos;
                float[] rows = [inverse * -sin, inverse * cos, w.Speed * .15915493667125702f,
                    x, -y, w.Amplitude, -(inverse * x), -(inverse * -y)];
                for (int row = 0; row < 8; row++)
                {
                    if (!float.IsFinite(rows[row])) throw new InvalidDataException("Native water wave constants exceed the finite preview range.");
                    output[template * 32 + row * 4 + wave] = rows[row];
                }
            }
        return output;
    }
}

public sealed record CompiledWaterWave(float Amplitude, float WaveLength, float Angle, float Speed, float Pinch);
public sealed record CompiledWaterWaveTemplate(IReadOnlyList<CompiledWaterWave> Waves);
