using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NewAxis.Services;

public class IniFileParser
{
    private readonly Dictionary<string, Dictionary<string, string>> _data = new(StringComparer.OrdinalIgnoreCase);

    public void Load(string path)
    {
        if (File.Exists(path))
        {
            var lines = File.ReadAllLines(path);
            ParseLines(lines);
        }
    }

    public void Load(byte[] data)
    {
        if (data == null || data.Length == 0) return;

        using (var stream = new MemoryStream(data))
        using (var reader = new StreamReader(stream))
        {
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }
            ParseLines(lines);
        }
    }

    private void ParseLines(IEnumerable<string> lines)
    {
        _data.Clear();
        string currentGroup = string.Empty;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";") || line.StartsWith("#"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                currentGroup = line.Substring(1, line.Length - 2).Trim();
                if (!_data.ContainsKey(currentGroup))
                {
                    _data[currentGroup] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex > 0)
            {
                var key = line.Substring(0, equalsIndex).Trim();
                var value = line.Substring(equalsIndex + 1).Trim();

                if (!_data.ContainsKey(currentGroup))
                {
                    _data[currentGroup] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                _data[currentGroup][key] = value;
            }
        }
    }

    public string? GetValue(string group, string key)
    {
        if (_data.TryGetValue(group, out var groupData))
        {
            if (groupData.TryGetValue(key, out var value))
            {
                return value;
            }
        }
        return null;
    }

    public void SetValue(string group, string key, string value)
    {
        if (!_data.ContainsKey(group))
        {
            _data[group] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        _data[group][key] = value;
    }

    public void Save(string path)
    {
        var content = BuildContent();
        File.WriteAllText(path, content);
    }

    public byte[] GetFileBytes()
    {
        var content = BuildContent();
        return Encoding.UTF8.GetBytes(content);
    }

    private string BuildContent()
    {
        var sb = new StringBuilder();

        if (_data.TryGetValue(string.Empty, out var globalKeys))
        {
            foreach (var kvp in globalKeys)
            {
                sb.AppendLine($"{kvp.Key}={kvp.Value}");
            }
            if (globalKeys.Count > 0) sb.AppendLine();
        }

        foreach (var group in _data.Keys)
        {
            if (string.IsNullOrEmpty(group)) continue;

            sb.AppendLine($"[{group}]");
            foreach (var kvp in _data[group])
            {
                sb.AppendLine($"{kvp.Key}={kvp.Value}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
