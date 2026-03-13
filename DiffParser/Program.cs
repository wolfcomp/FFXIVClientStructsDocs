using System.Buffers;
using System.Diagnostics;
using DiffParser.Helpers;

var structAndEnumSearchValue = SearchValues.Create(["struct", "enum"], StringComparison.Ordinal);

if (args.Length < 4)
{
    Console.WriteLine("""
    Expected args passed:
    DiffParser.exe {gitPath} {base} {compare} {outputFile}
    """);
    throw new ArgumentException("Bad argument length", "args");
}

var currentDirectory = Directory.GetCurrentDirectory();
var gitProjectPath = new DirectoryInfo(Path.Combine(currentDirectory, args[0])).FullName;

var startInfo = new ProcessStartInfo
{
    UseShellExecute = false,
    CreateNoWindow = true,
    WindowStyle = ProcessWindowStyle.Hidden,
    FileName = "git",
    Arguments = "--version",
    RedirectStandardOutput = true,
    WorkingDirectory = gitProjectPath
};

var process = new Process
{
    StartInfo = startInfo
};

process.Start();
var output = process.StandardOutput.ReadToEnd();
process.WaitForExit();

if (!output.StartsWith("git version"))
    throw new InvalidDataException("Could not get correct data from git");

var gitBase = args[1];
var gitCompare = args[2];
startInfo.Arguments = $"diff {gitBase} {gitCompare} --name-status *.cs :^CExporter/ :^ExcelGenerator/ :^CompatChecker/ :^FFXIVClientStructs/Attributes/ :^InteropGenerator.Tests/ :^InteropGenerator/";
process = new Process
{
    StartInfo = startInfo
};

process.Start();
output = process.StandardOutput.ReadToEnd();
process.WaitForExit();
var groupedFiles = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split('\t')).GroupBy(f => f[0]).ToDictionary(t => t.Key, t => t.Select(f => f.Last()).ToArray());
var groupedFileChanges = new List<NamespaceChange>();
foreach (var (type, files) in groupedFiles)
{
    foreach (var file in files)
    {
        groupedFileChanges.Add(GetChanges(file));
    }
}
groupedFileChanges = groupedFileChanges.GroupBy(t => t.diffNamespace).Select(t => new NamespaceChange(t.Key, t.SelectMany(f => f.changes.Where(k => k.HasChanges)).ToList())).ToList();
var writeFile = new FileInfo(Path.Combine(currentDirectory, args[3]));
var indentedWriter = new IndentedTextWriter();
foreach (var (diffNamespace, fileChanges) in groupedFileChanges)
{
    if (!fileChanges.Any()) continue;
    indentedWriter.WriteLine($"diff --git a/{diffNamespace}.cs b/{diffNamespace}.cs");
    indentedWriter.WriteLine($"--- a/{diffNamespace}.cs");
    indentedWriter.WriteLine($"+++ b/{diffNamespace}.cs");
    indentedWriter.WriteLine("/");
    foreach (var (diffObject, type, deletions, additions, file) in fileChanges)
    {
        indentedWriter.WriteLine($"{type} {diffObject}", diff: ' ');
        indentedWriter.IncreaseIndent();
        var highestIndex = Math.Max(deletions.LastOrDefault()?.line ?? 0, additions.LastOrDefault()?.line ?? 0);
        var index = 0;
        var lastDeletionCheck = 0;
        var lastAdditionCheck = 0;
        while (index < highestIndex + 1)
        {
            if (deletions.Count > 0 && deletions.Count > lastDeletionCheck && deletions[lastDeletionCheck].line == index)
            {
                if (!deletions[lastDeletionCheck].change.StartsWith("[MemberFunction") && !deletions[lastDeletionCheck].change.StartsWith("[StaticAddress") && !deletions[lastDeletionCheck].change.StartsWith("/"))
                    indentedWriter.WriteLine(deletions[lastDeletionCheck].change, diff: '-');
                lastDeletionCheck++;
            }
            if (additions.Count > 0 && additions.Count > lastAdditionCheck && additions[lastAdditionCheck].line == index)
            {
                if (!additions[lastAdditionCheck].change.StartsWith("[MemberFunction") && !additions[lastAdditionCheck].change.StartsWith("[StaticAddress") && !additions[lastAdditionCheck].change.StartsWith("/"))
                    indentedWriter.WriteLine(additions[lastAdditionCheck].change, diff: '+');
                lastAdditionCheck++;
            }
            index++;
        }
        indentedWriter.DecreaseIndent();
    }
}
File.WriteAllText(writeFile.FullName, indentedWriter.ToString());

// TODO: Write it out as an actual diff file
/*
* Diff file pattern needs to follow:
* ```
* diff --git a/<path> b/<path>
* --- a/<path>
* +++ b/<path>
* <content>
* ```
* Else diff viewers doesn't understand the diff
* <path> will be made to be `$"{groupedFilesChange.diffNamespace}.cs"`
* <content> will be made as following:
* ```
* public {change.type} {change.diffObject} {
*     {change.changes}
* }
* ```
*/

NamespaceChange GetChanges(string file)
{
    startInfo.Arguments = $"diff {gitBase} {gitCompare} -U9999 -- {file}";
    process = new Process
    {
        StartInfo = startInfo
    };
    process.Start();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    var lines = output.Split('\n')[5..^1];
    var line = 0;
    while (!lines[line][1..].StartsWith("namespace"))
        line++;
    var fileNamespace = lines[line].Split(' ')[^1][..^1];
    var fileChanges = new List<ObjectChange>();
    while (line < lines.Length)
    {
        var (diffObjectStartLine, diffObjectEndLine) = GetObjectPosition(lines, line);
        if (diffObjectEndLine == diffObjectStartLine) break;
        var diffObject = GetDiffObject(lines[diffObjectStartLine]);
        var diffType = lines[diffObjectStartLine].Split(' ')[lines[diffObjectStartLine].Split(' ').IndexOf(diffObject) - 1];
        fileChanges.AddRange(GetObjectDiff(diffObject, diffType, lines[(diffObjectStartLine + 1)..diffObjectEndLine], diffObjectStartLine + 1, file));
        line = diffObjectEndLine;
    }
    return new NamespaceChange(fileNamespace, fileChanges);
}

List<ObjectChange> GetObjectDiff(string objectName, string type, string[] lines, int lineOffset, string file)
{
    for (int i = 0; i < lines.Length; i++)
    {
        if (lines[i].Length > 5 && lines[i][1..].StartsWith("    "))
            lines[i] = lines[i][0] + lines[i][5..];
    }
    var deletions = new List<LineChange>();
    var additions = new List<LineChange>();
    var ret = new List<ObjectChange>();
    var line = 0;
    while (line < lines.Length)
    {
        if (string.IsNullOrWhiteSpace(lines[line][1..].Trim())) line++;
        else if (lines[line].StartsWith('-'))
            deletions.Add(new LineChange(line + lineOffset, lines[line++][1..]));
        else if (lines[line].StartsWith('+'))
            additions.Add(new LineChange(line + lineOffset, lines[line++][1..]));
        else if (lines[line].StartsWith(" //"))
            line++;
        else if (lines[line].ContainsAny(structAndEnumSearchValue))
        {
            var (diffObjectStartLine, diffObjectEndLine) = GetObjectPosition(lines, line);
            if (diffObjectEndLine == diffObjectStartLine)
            {
                line++;
                continue;
            }
            var diffObject = GetDiffObject(lines[diffObjectStartLine]);
            var diffType = lines[diffObjectStartLine].Split(' ')[lines[diffObjectStartLine].Split(' ').IndexOf(diffObject) - 1];
            ret.AddRange(GetObjectDiff($"{objectName}.{diffObject}", diffType, lines[(diffObjectStartLine + 1)..diffObjectEndLine], diffObjectStartLine + 1 + lineOffset, file));
            line = diffObjectEndLine;
        }
        else 
            line++;
    }
    ret.Add(new ObjectChange(objectName, type, deletions, additions, file));
    return ret;
}

(int start, int end) GetObjectPosition(Span<string> lines, int needle)
{
    if (lines[needle].EndsWith(';') && lines[needle].ContainsAny(structAndEnumSearchValue)) return (needle, needle);
    try
    {
        while (!(lines[needle].ContainsAny(structAndEnumSearchValue) && !lines[needle].EndsWith(';')) || lines[needle].StartsWith(" /"))
            needle++;
        var diffObjectStartLine = needle;
        while (!lines[needle].AsSpan().SequenceEqual(" }"))
            needle++;
        return (diffObjectStartLine, needle);
    }
    catch
    {
        return (0,0);
    }
}

string GetDiffObject(string line)
{
    var commentIndex = line.IndexOf('/');
    var substrIndex = commentIndex > 0 ? commentIndex : line.Length;
    var objs = line[..substrIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var extendIndex = line.IndexOf(':');
    if (extendIndex > 0 && extendIndex < substrIndex)
        return objs[objs.IndexOf(":")-1];
    return objs[^2];
}

record LineChange(int line, string change)
{
    public override string ToString()
    {
        return $"{line}: {change}";
    }
}

record ObjectChange(string diffObject, string type, List<LineChange> deletions, List<LineChange> additions, string file)
{
    public bool HasChanges => (deletions.Count > 0 || additions.Count > 0) && 
        deletions.Any(t => !(t.change.StartsWith("[MemberFunction") || t.change.StartsWith("[StaticAddress") || t.change.StartsWith("/"))) && 
        additions.Any(t => !(t.change.StartsWith("[MemberFunction") || t.change.StartsWith("[StaticAddress") || t.change.StartsWith("/")));

    public FormattableString GetFormattableString()
    {
        var formattable = new FormattableString(2);
        formattable.Append($"{diffObject}: |");
        var highestIndex = Math.Max(deletions.LastOrDefault()?.line ?? 0, additions.LastOrDefault()?.line ?? 0);
        var index = 0;
        var lastDeletionCheck = 0;
        var lastAdditionCheck = 0;
        while (index < highestIndex + 1)
        {
            if (deletions.Count > 0 && deletions.Count > lastDeletionCheck && deletions[lastDeletionCheck].line == index)
            {
                formattable.AppendIndent($"-{deletions[lastDeletionCheck].change}");
                lastDeletionCheck++;
            }
            if (additions.Count > 0 && additions.Count > lastAdditionCheck && additions[lastAdditionCheck].line == index)
            {
                formattable.AppendIndent($"+{additions[lastAdditionCheck].change}");
                lastAdditionCheck++;
            }
            index++;
        }
        return formattable;
    }
}

record NamespaceChange(string diffNamespace, List<ObjectChange> changes)
{
    public FormattableString GetFormattableString()
    {
        var formattable = new FormattableString(2);
        formattable.Append($"{diffNamespace}:");
        foreach(var change in changes)
            if (change.HasChanges)
                formattable.AppendIndent(change.GetFormattableString());
        return formattable;
    }
}

class FormattableString
{
    private int _spacing;
    private List<string> _lines;

    public FormattableString(int spacing = 4)
    {
        _spacing = spacing;
        _lines = new List<string>();
    }

    public void Append(string line)
    {
        _lines.Add(line);
    }

    public void Append(List<string> lines)
    {
        foreach(var line in lines)
            _lines.Add(line);
    }

    public void Append(FormattableString formattable)
    {
        foreach(var line in formattable._lines)
            _lines.Add(line);
    }

    public void Append(List<FormattableString> formattables)
    {
        foreach(var formattable in formattables)
            foreach(var line in formattable._lines)
                _lines.Add(line);
    }

    public void AppendIndent(string line)
    {
        _lines.Add(new string(' ', _spacing) + line);
    }

    public void AppendIndent(List<string> lines)
    {
        foreach(var line in lines)
            _lines.Add(new string(' ', _spacing) + line);
    }

    public void AppendIndent(FormattableString formattable)
    {
        foreach(var line in formattable._lines)
            _lines.Add(new string(' ', _spacing) + line);
    }

    public void AppendIndent(List<FormattableString> formattables)
    {
        foreach(var formattable in formattables)
            foreach(var line in formattable._lines)
                _lines.Add(new string(' ', _spacing) + line);
    }

    public override string ToString()
    {
        return string.Join('\n', _lines);
    }
}