using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Xml;
using DiffParser.Helpers;
using YamlDotNet.Serialization;

var currentDirectory = Directory.GetCurrentDirectory();
var outputDirecotry = Path.Combine(currentDirectory, "output");
Directory.CreateDirectory(outputDirecotry);

if (args.Length > 0 && string.Equals(args[0], "analyze-diff", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2)
    {
        Console.WriteLine("Expected args passed:\nDiffParser.exe analyze-diff {diffFile} [outputFile]");
        throw new ArgumentException("Bad argument length", nameof(args));
    }

    var diffFilePath = Path.IsPathRooted(args[1]) ? args[1] : Path.Combine(currentDirectory, args[1]);
    var outputFilePath = args.Length > 2
        ? (Path.IsPathRooted(args[2]) ? args[2] : Path.Combine(outputDirecotry, args[2]))
        : Path.Combine(Path.GetDirectoryName(diffFilePath)!, $"{Path.GetFileNameWithoutExtension(diffFilePath)}.breaking.md");

    if (!File.Exists(diffFilePath))
        throw new FileNotFoundException($"Could not find diff file at '{diffFilePath}'.");

    Console.WriteLine($"Analyzing diff: {diffFilePath}");
    var diffContent = File.ReadAllText(diffFilePath);
    var report = BreakingChangeAnalyzer.AnalyzeDiffToMarkdown(diffContent, diffFilePath);
    File.WriteAllText(outputFilePath, report);
    Console.WriteLine($"Wrote breaking-change report to: {outputFilePath}");
    return;
}

var structAndEnumSearchValue = SearchValues.Create(["struct", "enum"], StringComparison.Ordinal);
var generatedTypesSearchValue = new string[] { "Delegates", "Addresses", "MemberFunctionPointers", "VirtualTable" };

if (args.Length < 4)
{
    Console.WriteLine("""
    Expected args passed:
    DiffParser.exe {gitPath} {base} {compare} {outputFile}
    """);
    throw new ArgumentException("Bad argument length", "args");
}

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

Console.WriteLine("Cheking if git exists");

process.Start();
var output = process.StandardOutput.ReadToEnd();
process.WaitForExit();

if (!output.StartsWith("git version"))
    throw new InvalidDataException("Could not get correct data from git");

var gitBase = args[1];
var gitCompare = args[2];
startInfo.Arguments = $"diff {gitBase}...{gitCompare} -U9999 *.cs :^CExporter/ :^ExcelGenerator/ :^CompatChecker/ :^FFXIVClientStructs/Attributes/ :^InteropGenerator.Tests/ :^InteropGenerator/";
process = new Process
{
    StartInfo = startInfo
};

Console.WriteLine("Getting changed file list");

process.Start();
output = process.StandardOutput.ReadToEnd();
process.WaitForExit();
output = output.Replace("FFXIVClientStructs/", "FFXIVClientStructs/FFXIVClientStructs/");
// var groupedFiles = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split('\t')).GroupBy(f => f[0]).ToDictionary(t => t.Key, t => t.Select(f => f.Last()).ToArray());
// var groupedFileChanges = new List<NamespaceChange>();
//
// Console.WriteLine("Getting changes in files:");
// foreach (var (type, files) in groupedFiles)
// {
//     foreach (var file in files)
//     {
//         Console.WriteLine($"  {file}");
//         groupedFileChanges.Add(GetChanges(file));
//     }
// }
// groupedFileChanges = groupedFileChanges.GroupBy(t => t.diffNamespace).Select(t => new NamespaceChange(t.Key, t.SelectMany(f => f.changes.Where(k => k.HasChanges)).ToList())).ToList();
var writeFile = new FileInfo(Path.Combine(outputDirecotry, args[3]));
// var indentedWriter = new IndentedTextWriter();
//
// Console.WriteLine("Parsing diff of:");
// foreach (var (diffNamespace, fileChanges) in groupedFileChanges)
// {
//     if (!fileChanges.Any()) continue;
//     indentedWriter.WriteLine($"diff --git a/{diffNamespace}.cs b/{diffNamespace}.cs");
//     indentedWriter.WriteLine($"--- a/{diffNamespace}.cs");
//     indentedWriter.WriteLine($"+++ b/{diffNamespace}.cs");
//     indentedWriter.WriteLine("/");
//     foreach (var (diffObject, type, deletions, additions, file) in fileChanges)
//     {
//         Console.WriteLine($"  {file}");
//         indentedWriter.WriteLine($"{type} {diffObject}", diff: ' ');
//         indentedWriter.IncreaseIndent();
//         var highestIndex = Math.Max(deletions.LastOrDefault()?.line ?? 0, additions.LastOrDefault()?.line ?? 0);
//         var index = 0;
//         var lastDeletionCheck = 0;
//         var lastAdditionCheck = 0;
//         while (index < highestIndex + 1)
//         {
//             if (deletions.Count > 0 && deletions.Count > lastDeletionCheck && deletions[lastDeletionCheck].line == index)
//             {
//                 if (!deletions[lastDeletionCheck].change.StartsWith("[MemberFunction") && !deletions[lastDeletionCheck].change.StartsWith("[StaticAddress") && !deletions[lastDeletionCheck].change.StartsWith("/"))
//                     indentedWriter.WriteLine(deletions[lastDeletionCheck].change, diff: '-');
//                 lastDeletionCheck++;
//             }
//             if (additions.Count > 0 && additions.Count > lastAdditionCheck && additions[lastAdditionCheck].line == index)
//             {
//                 if (!additions[lastAdditionCheck].change.StartsWith("[MemberFunction") && !additions[lastAdditionCheck].change.StartsWith("[StaticAddress") && !additions[lastAdditionCheck].change.StartsWith("/"))
//                     indentedWriter.WriteLine(additions[lastAdditionCheck].change, diff: '+');
//                 lastAdditionCheck++;
//             }
//             index++;
//         }
//         indentedWriter.DecreaseIndent();
//     }
// }
File.WriteAllText(writeFile.FullName, output);

Console.WriteLine("Builing project to get obsoletes");
startInfo.Arguments = @"build .\FFXIVClientStructs\FFXIVClientStructs.csproj -c Release";
startInfo.FileName = "dotnet";
startInfo.RedirectStandardOutput = false;
process.Start();
process.WaitForExit();

Console.WriteLine("Loading project to get obsoletes");
var ffxivClientStructsAssemblyDir = Path.Combine(gitProjectPath, "bin", "Release");
var ffxivClientStructsAssembly = Path.Combine(ffxivClientStructsAssemblyDir, "FFXIVClientStructs.dll");
var ffxivClientStructsXmlDoc = Path.Combine(ffxivClientStructsAssemblyDir, "FFXIVClientStructs.xml");
var allAssemblyPaths = new List<string>();

foreach (string file in Directory.GetFiles(ffxivClientStructsAssemblyDir, "*.dll"))
    allAssemblyPaths.Add(file);

foreach (string file in Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"))
    allAssemblyPaths.Add(file);

var resolver = new PathAssemblyResolver(allAssemblyPaths);
var mlc = new MetadataLoadContext(resolver);
var assembly = mlc.LoadFromAssemblyPath(ffxivClientStructsAssembly);
Console.WriteLine("Querying project to get obsoletes");
var obsoleteMembers = GetAllObsoleteMembers(GetApiTypes(assembly.GetTypes())).Select(t =>
{
    var (message, error) = GetObsoleteValues(t.CustomAttributes.First(f => f.AttributeType.Name == nameof(ObsoleteAttribute)));
    return Tuple.Create(t, message, error);
}).Where(t => t.Item2 != "Types with embedded references are not supported in this version of your compiler.").ToList();
var obsoleteDict = new Dictionary<string, List<string?>>();
var xmlDocument = new XmlDocument();
xmlDocument.Load(ffxivClientStructsXmlDoc);
var xmlBaseElement = xmlDocument.DocumentElement;
var xmlMembers = xmlBaseElement!.SelectSingleNode("members")!;
var xmlMembers2 = xmlMembers.ChildNodes.Cast<XmlNode>().ToDictionary(t => t.Attributes["name"].InnerText[2..], t => t.ChildNodes.Cast<XmlNode>().ToList());

Console.WriteLine("Parsing obsoletes");
foreach (var (obsoleteMember, message, error) in obsoleteMembers)
{
    if (message == "Types with embedded references are not supported in this version of your compiler.") continue;
    if (error)
    {
        var declaringMember = obsoleteMember.DeclaringType;
        if (declaringMember is not null and not { Name: "Delegates" } or { IsEnum: true })
        {
            if (xmlMembers2.TryGetValue(GetMemberXmlDoc(obsoleteMember), out var member) && member.Any(t => t.Name == "inheritdoc")) continue;
            if (obsoleteDict.TryGetValue(declaringMember.Name, out var obsoleteList))
                obsoleteList.Add(GetMemberName(obsoleteMember));
            else
                obsoleteDict[declaringMember.Name] = [GetMemberName(obsoleteMember)];
        }
        else if (declaringMember is { IsEnum: true })
        {
            obsoleteDict[obsoleteMember.Name] = [];
        }
    }
}

obsoleteDict = obsoleteDict.OrderBy(t => t.Key).ToDictionary();
var serializer = new SerializerBuilder().Build();
var obsoleteWriteFile = new FileInfo($"{writeFile.FullName.TrimEnd(writeFile.Extension)}.obsolete.yml");
var letterObsoleteDict = obsoleteDict.GroupBy(t => t.Key[0]).ToDictionary(t => t.Key, t => t.ToList());
File.WriteAllText(obsoleteWriteFile.FullName, serializer.Serialize(letterObsoleteDict));

string GetMemberName(MemberInfo member)
{
    if (member.MemberType == MemberTypes.Method)
    {
        var methodBase = (MethodBase)member;
        var methodParams = string.Join(',', methodBase.GetParameters().Select(t => t.ParameterType.FullName));
        return $"{member.Name}({methodParams})";
    }
    return member.Name;
}

string GetMemberXmlDoc(MemberInfo member)
{
    if (member.MemberType == MemberTypes.Method)
    {
        var methodBase = (MethodBase)member;
        var methodParams = string.Join(',', methodBase.GetParameters().Select(t => t.ParameterType.FullName));
        return $"{member.DeclaringType!.FullName}.{member.Name}({methodParams})";
    }
    return member.DeclaringType!.FullName + "." + member.Name;
}

Type[] GetApiTypes(Type[] types) =>
    types.Where(t =>
        t != typeof(decimal) &&
        !t.IsPrimitive &&
        !IsGeneratedTypeName(t.Name)).ToArray();

(string ObsoleteMessage, bool IsError) GetObsoleteValues(CustomAttributeData attributeData)
{
    var obsoleteMessage = attributeData.ConstructorArguments.Count > 0
        ? attributeData.ConstructorArguments[0].Value?.ToString() ?? string.Empty
        : string.Empty;

    var isError = false;
    if (attributeData.ConstructorArguments.Count >= 2 && attributeData.ConstructorArguments[1].Value is bool ctorError)
        isError = ctorError;

    if (!isError)
    {
        var namedIsError = attributeData.NamedArguments.FirstOrDefault(arg => string.Equals(arg.MemberName, "IsError", StringComparison.Ordinal));
        if (namedIsError.TypedValue.Value is bool namedError)
            isError = namedError;
    }

    return (obsoleteMessage, isError);
}

MemberInfo[] GetAllObsoleteMembers(Type[] types)
{
    MemberInfo[] obsoletes = [.. types
        .Where(t => !IsGeneratedTypeName(t.Name) && t.CustomAttributes.Any(f => f.AttributeType.Name == nameof(ObsoleteAttribute)))
        .Cast<MemberInfo>()];

    foreach (var type in types)
    {
        if (IsGeneratedTypeName(type.Name)) continue;

        // Only include members declared on this exact type. This prevents inherited
        // obsolete members from being exported repeatedly for every derived type.
        var members = type.GetMembers(
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly);
        var obsoletedMembers = members.Where(member =>
            !IsGeneratedTypeName(member.DeclaringType?.Name) &&
            member.CustomAttributes.Any(attr => attr.AttributeType.Name == nameof(ObsoleteAttribute)));

        obsoletes = [.. obsoletes, .. obsoletedMembers];
    }

    return obsoletes;
}

bool IsGeneratedTypeName(string? typeName)
{
    if (string.IsNullOrWhiteSpace(typeName))
        return false;
    return generatedTypesSearchValue.Any(suffix => typeName.EndsWith(suffix, StringComparison.Ordinal));
}

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
        return (0, 0);
    }
}

string GetDiffObject(string line)
{
    var commentIndex = line.IndexOf('/');
    var substrIndex = commentIndex > 0 ? commentIndex : line.Length;
    var objs = line[..substrIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var extendIndex = line.IndexOf(':');
    if (extendIndex > 0 && extendIndex < substrIndex)
        return objs[objs.IndexOf(":") - 1];
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
        foreach (var change in changes)
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
        foreach (var line in lines)
            _lines.Add(line);
    }

    public void Append(FormattableString formattable)
    {
        foreach (var line in formattable._lines)
            _lines.Add(line);
    }

    public void Append(List<FormattableString> formattables)
    {
        foreach (var formattable in formattables)
            foreach (var line in formattable._lines)
                _lines.Add(line);
    }

    public void AppendIndent(string line)
    {
        _lines.Add(new string(' ', _spacing) + line);
    }

    public void AppendIndent(List<string> lines)
    {
        foreach (var line in lines)
            _lines.Add(new string(' ', _spacing) + line);
    }

    public void AppendIndent(FormattableString formattable)
    {
        foreach (var line in formattable._lines)
            _lines.Add(new string(' ', _spacing) + line);
    }

    public void AppendIndent(List<FormattableString> formattables)
    {
        foreach (var formattable in formattables)
            foreach (var line in formattable._lines)
                _lines.Add(new string(' ', _spacing) + line);
    }

    public override string ToString()
    {
        return string.Join('\n', _lines);
    }
}