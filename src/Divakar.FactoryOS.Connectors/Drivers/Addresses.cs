using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Divakar.FactoryOS.Connectors.Drivers;

public enum ModbusArea { HoldingRegister, InputRegister, Coil, DiscreteInput }

public enum ModbusType { Int16, UInt16, Int32, UInt32, Float32, Float64, Bool }

/// <summary>
/// A Modbus tag address: "hr:40001:float32", "ir:30010:int16", "coil:1", "di:5", optionally ":swap" for
/// devices that send the low word first. Numbers may be written in Modicon style (40001) or zero-based (0).
/// </summary>
public sealed record ModbusAddress(ModbusArea Area, int Offset, ModbusType Type, bool WordSwap)
{
    public int Registers => Type switch
    {
        ModbusType.Int32 or ModbusType.UInt32 or ModbusType.Float32 => 2,
        ModbusType.Float64 => 4,
        _ => 1,
    };

    public static ModbusAddress Parse(string address)
    {
        var parts = address.Trim().ToLowerInvariant().Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            throw new FormatException($"Modbus address '{address}' must look like hr:40001:float32.");
        }
        var area = parts[0] switch
        {
            "hr" or "holding" => ModbusArea.HoldingRegister,
            "ir" or "input" => ModbusArea.InputRegister,
            "coil" or "co" => ModbusArea.Coil,
            "di" or "discrete" => ModbusArea.DiscreteInput,
            _ => throw new FormatException($"Unknown Modbus area '{parts[0]}' (use hr, ir, coil or di)."),
        };
        var offset = area switch
        {
            ModbusArea.HoldingRegister when n >= 40001 => n - 40001,
            ModbusArea.InputRegister when n >= 30001 => n - 30001,
            ModbusArea.DiscreteInput when n >= 10001 => n - 10001,
            ModbusArea.Coil when n >= 1 && parts.Length > 1 && parts[1].Length >= 5 => n - 1,
            _ => n,
        };
        var type = area is ModbusArea.Coil or ModbusArea.DiscreteInput ? ModbusType.Bool : ModbusType.UInt16;
        if (parts.Length > 2 && type != ModbusType.Bool)
        {
            type = parts[2] switch
            {
                "int16" or "short" => ModbusType.Int16,
                "uint16" or "word" => ModbusType.UInt16,
                "int32" or "dint" => ModbusType.Int32,
                "uint32" or "dword" => ModbusType.UInt32,
                "float32" or "float" or "real" => ModbusType.Float32,
                "float64" or "double" => ModbusType.Float64,
                _ => throw new FormatException($"Unknown Modbus data type '{parts[2]}'."),
            };
        }
        return new ModbusAddress(area, offset, type, parts.Contains("swap"));
    }

    /// <summary>Turns big-endian registers (Modbus order) into a number.</summary>
    public double Decode(ReadOnlySpan<ushort> regs)
    {
        Span<ushort> r = stackalloc ushort[Registers];
        regs[..Registers].CopyTo(r);
        if (WordSwap && Registers > 1)
        {
            r.Reverse();
        }
        Span<byte> bytes = stackalloc byte[Registers * 2];
        for (var i = 0; i < Registers; i++)
        {
            bytes[i * 2] = (byte)(r[i] >> 8);
            bytes[i * 2 + 1] = (byte)r[i];
        }
        if (BitConverter.IsLittleEndian)
        {
            bytes.Reverse();
        }
        return Type switch
        {
            ModbusType.Int16 => BitConverter.ToInt16(bytes),
            ModbusType.UInt16 => BitConverter.ToUInt16(bytes),
            ModbusType.Int32 => BitConverter.ToInt32(bytes),
            ModbusType.UInt32 => BitConverter.ToUInt32(bytes),
            ModbusType.Float32 => BitConverter.ToSingle(bytes),
            ModbusType.Float64 => BitConverter.ToDouble(bytes),
            _ => r[0],
        };
    }
}

/// <summary>An MQTT tag address: "topic" (payload is a number) or "topic|$.path.to[0].value" (payload is JSON).</summary>
public sealed record MqttAddress(string Topic, string? JsonPath)
{
    public static MqttAddress Parse(string address)
    {
        var i = address.IndexOf('|');
        return i < 0 ? new MqttAddress(address.Trim(), null) : new MqttAddress(address[..i].Trim(), address[(i + 1)..].Trim());
    }

    /// <summary>The number in a payload, or null when there is none at the path.</summary>
    public double? Extract(string payload)
    {
        if (JsonPath is null)
        {
            return ParseNumber(payload.Trim());
        }
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var el = doc.RootElement;
            foreach (Match m in Regex.Matches(JsonPath.TrimStart('$'), @"\.([^.\[]+)|\[(\d+)\]"))
            {
                if (m.Groups[1].Success)
                {
                    if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(m.Groups[1].Value, out el))
                    {
                        return null;
                    }
                }
                else
                {
                    var idx = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (el.ValueKind != JsonValueKind.Array || idx >= el.GetArrayLength())
                    {
                        return null;
                    }
                    el = el[idx];
                }
            }
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                JsonValueKind.String => ParseNumber(el.GetString() ?? ""),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ParseNumber(string s) => s.ToLowerInvariant() switch
    {
        "true" or "on" => 1,
        "false" or "off" => 0,
        _ => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
    };

    /// <summary>MQTT topic filter matching with + and # wildcards.</summary>
    public static bool Matches(string filter, string topic)
    {
        var f = filter.Split('/');
        var t = topic.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#")
            {
                return true;
            }
            if (i >= t.Length || (f[i] != "+" && f[i] != t[i]))
            {
                return false;
            }
        }
        return f.Length == t.Length;
    }
}

/// <summary>A Siemens S7 address with its type: "DB1.DBD0:real", "DB1.DBW4:int", "DB2.DBX0.1", "MW10", "I0.0".</summary>
public sealed record S7Address(string Address, string Type)
{
    private static readonly Regex Shape = new(@"^(DB\d+\.DB[XBWD]\d+(\.[0-7])?|[MIQEA][BWD]?\d+(\.[0-7])?)$", RegexOptions.IgnoreCase);

    public static S7Address Parse(string address)
    {
        var parts = address.Trim().Split(':');
        var addr = parts[0].ToUpperInvariant();
        if (!Shape.IsMatch(addr))
        {
            throw new FormatException($"S7 address '{address}' must look like DB1.DBD0:real, DB1.DBW2:int, DB1.DBX0.0 or MW10.");
        }
        var type = parts.Length > 1 ? parts[1].ToLowerInvariant()
            : addr.Contains(".DBX") || addr.Contains('.') ? "bool"
            : addr.Contains("DBD") || addr.StartsWith("MD") ? "dint"
            : addr.Contains("DBW") || addr.StartsWith("MW") ? "int"
            : "byte";
        if (type is not ("real" or "int" or "word" or "dint" or "dword" or "bool" or "byte"))
        {
            throw new FormatException($"Unknown S7 type '{type}' (use real, int, word, dint, dword, bool or byte).");
        }
        return new S7Address(addr, type);
    }
}

/// <summary>Reads simple key/value settings from an asset's ConnectorSettings JSON.</summary>
public sealed class Settings
{
    private readonly Dictionary<string, JsonElement> values = new(StringComparer.OrdinalIgnoreCase);

    public Settings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            values[p.Name] = p.Value.Clone();
        }
    }

    public string? String(string key) => values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public int Int(string key, int fallback) => values.TryGetValue(key, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    public bool Bool(string key, bool fallback) =>
        values.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
}

public static class Endpoints
{
    /// <summary>"host:port", "tcp://host:port" or just "host" into host and port.</summary>
    public static (string Host, int Port) HostPort(string? endpoint, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new FormatException("The asset has no endpoint (address of the device).");
        }
        var e = Regex.Replace(endpoint.Trim(), "^[a-z]+://", "", RegexOptions.IgnoreCase).TrimEnd('/');
        var slash = e.IndexOf('/');
        if (slash >= 0)
        {
            e = e[..slash];
        }
        var colon = e.LastIndexOf(':');
        return colon > 0 && int.TryParse(e[(colon + 1)..], out var port) ? (e[..colon], port) : (e, defaultPort);
    }
}
