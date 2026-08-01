using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CloudReEdit.Services;

/// <summary>
/// Patches a single app's playtime block inside localconfig.vdf while leaving
/// every other byte untouched. Handles both \n and \r\n line endings.
/// </summary>
public static class VdfEditor
{
    private static readonly Regex KeyValueRegex =
        new(@"^(\s*)""([^""]+)""(\s*)""([^""]*)""(\s*)$", RegexOptions.Compiled);

    private static readonly Regex BareKeyRegex =
        new(@"^(\s*)""([^""]+)""(\s*)$", RegexOptions.Compiled);

    public static bool PatchAppPlaytime(string vdfPath, string appId,
        long playtime, long lastPlayed)
    {
        string raw = File.ReadAllText(vdfPath);
        bool crlf = raw.Contains("\r\n");
        bool hasTrailingNewline = raw.EndsWith("\n");

        var lines = raw.Split('\n');
        var block = FindAppBlock(lines, appId);
        if (block == null) return false;

        var (start, end, braceIndent) = block.Value;
        var result = new List<string>();
        bool replacedPlaytime = false;
        bool replacedLastPlayed = false;
        string nl = crlf ? "\r" : "";

        for (int i = 0; i < lines.Length; i++)
        {
            if (i == end)
            {
                if (!replacedPlaytime)
                    result.Add($"{braceIndent}\t\"Playtime\"\t\t\"{playtime}\"{nl}");
                if (!replacedLastPlayed)
                    result.Add($"{braceIndent}\t\"LastPlayed\"\t\t\"{lastPlayed}\"{nl}");
                result.Add(lines[i]);
                continue;
            }

            if (i > start && i < end)
            {
                if (TryParseKeyValue(lines[i], out var indent, out var key, out _))
                {
                    if (key == "Playtime")
                    {
                        result.Add($"{indent}\"Playtime\"\t\t\"{playtime}\"{nl}");
                        replacedPlaytime = true;
                        continue;
                    }
                    if (key == "LastPlayed")
                    {
                        result.Add($"{indent}\"LastPlayed\"\t\t\"{lastPlayed}\"{nl}");
                        replacedLastPlayed = true;
                        continue;
                    }
                    if (key == "Playtime2wks")
                    {
                        result.Add($"{indent}\"Playtime2wks\"\t\t\"{playtime}\"{nl}");
                        continue;
                    }
                }
                result.Add(lines[i]);
                continue;
            }

            result.Add(lines[i]);
        }

        string joined = string.Join("\n", result);
        if (hasTrailingNewline && !joined.EndsWith("\n"))
            joined += "\n";

        FileUtils.AtomicWriteAllText(vdfPath, joined);
        return true;
    }

    public static bool TryGetAppValues(string vdfPath, string appId,
        out long playtime, out long lastPlayed)
    {
        playtime = 0;
        lastPlayed = 0;
        if (!File.Exists(vdfPath)) return false;

        var lines = File.ReadAllLines(vdfPath);
        var block = FindAppBlock(lines, appId);
        if (block == null) return false;

        bool found = false;
        for (int i = block.Value.Start + 1; i < block.Value.End; i++)
        {
            if (!TryParseKeyValue(lines[i], out _, out var key, out var value)) continue;
            switch (key)
            {
                case "Playtime" when long.TryParse(value, out var p):
                    playtime = p;
                    found = true;
                    break;
                case "LastPlayed" when long.TryParse(value, out var lp):
                    lastPlayed = lp;
                    break;
            }
        }
        return found;
    }

    private static (int Start, int End, string BraceIndent)? FindAppBlock(
        IReadOnlyList<string> lines, string appId)
    {
        var stack = new Stack<string>();
        string? pendingKey = null;
        int? appBlockStart = null;
        string braceIndent = "";
        int depth = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();

            if (appBlockStart != null)
            {
                if (trimmed == "{")
                {
                    depth++;
                    continue;
                }
                if (trimmed == "}")
                {
                    depth--;
                    if (depth == 0)
                        return (appBlockStart.Value, i, braceIndent);
                    continue;
                }
                continue;
            }

            if (trimmed == "{")
            {
                string section = pendingKey ?? "";
                stack.Push(section);
                pendingKey = null;

                if (section == appId && stack.Count >= 2 &&
                    stack.Peek() == appId && GetParent(stack) == "apps")
                {
                    appBlockStart = i;
                    braceIndent = ExtractIndent(lines[i]);
                    depth = 1;
                }
                continue;
            }

            if (trimmed == "}")
            {
                if (stack.Count > 0) stack.Pop();
                pendingKey = null;
                continue;
            }

            if (BareKeyRegex.IsMatch(trimmed))
            {
                pendingKey = BareKeyRegex.Match(trimmed).Groups[2].Value;
                continue;
            }

            pendingKey = null;
        }

        return null;
    }

    private static string GetParent(Stack<string> stack)
    {
        string top = stack.Pop();
        string parent = stack.Count > 0 ? stack.Peek() : "";
        stack.Push(top);
        return parent;
    }

    private static string ExtractIndent(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return line.Substring(0, i);
    }

    private static bool TryParseKeyValue(string line, out string indent,
        out string key, out string value)
    {
        indent = "";
        key = "";
        value = "";
        string trimmed = line.TrimEnd('\r', '\n');
        var m = KeyValueRegex.Match(trimmed);
        if (!m.Success) return false;
        indent = m.Groups[1].Value;
        key = m.Groups[2].Value;
        value = m.Groups[4].Value;
        return true;
    }
}
