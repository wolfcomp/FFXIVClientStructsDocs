using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Generates a docs/breaking/{version}.md style report from a git diff of the FFXIVClientStructs submodule.
/// The full before/after contents of every changed file are loaded from the submodule using the blob hashes
/// in the diff's "index" lines, parsed with Roslyn and compared type by type.
/// </summary>
internal static class BreakingChangesDocGenerator {
    private const string Usage = """
        Expected args passed:
        DiffParser.exe breaking-docs {diffFile} [outputFile] [--repo {submodulePath}] [--commit {sha}]
          outputFile   defaults to docs/breaking/{diffFileName}.md
          --repo       path to the FFXIVClientStructs checkout, defaults to ./FFXIVClientStructs
          --commit     commit written to the "Updated to commit" line, defaults to the one already in outputFile
        """;

    private static readonly Regex HunkHeaderRegex = new(@"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@", RegexOptions.Compiled);
    private static readonly Regex CtorDtorRegex = new(@"^(Ctor|Dtor)\d*$", RegexOptions.Compiled);

    public static void Run(string[] args) {
        string? diffArg = null, outputArg = null, repoArg = null, commitArg = null;
        for (var i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "--repo" when i + 1 < args.Length: repoArg = args[++i]; break;
                case "--commit" when i + 1 < args.Length: commitArg = args[++i]; break;
                default:
                    if (diffArg is null) diffArg = args[i];
                    else if (outputArg is null) outputArg = args[i];
                    else throw new ArgumentException($"Unexpected argument '{args[i]}'.\n{Usage}");
                    break;
            }
        }

        if (diffArg is null) {
            Console.WriteLine(Usage);
            throw new ArgumentException("Bad argument length", nameof(args));
        }

        var diffPath = Path.GetFullPath(diffArg);
        var outputPath = Path.GetFullPath(outputArg ?? Path.Combine("docs", "breaking", $"{Path.GetFileNameWithoutExtension(diffPath)}.md"));
        var repoPath = Path.GetFullPath(repoArg ?? "FFXIVClientStructs");

        if (!File.Exists(diffPath))
            throw new FileNotFoundException($"Could not find diff file at '{diffPath}'.");
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Could not find FFXIVClientStructs checkout at '{repoPath}'. Pass --repo.");

        Console.WriteLine($"Parsing diff: {diffPath}");
        // Only the FFXIVClientStructs project is public API; skip tooling like CExporter and ExcelGenerator
        var fileDiffs = UnifiedDiff.Parse(File.ReadAllText(diffPath))
            .Where(f => (f.NewPath ?? f.OldPath)!.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => (f.NewPath ?? f.OldPath)!.StartsWith("FFXIVClientStructs/", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"  {fileDiffs.Count} changed .cs files");

        Console.WriteLine($"Loading file contents from: {repoPath}");
        var oldApi = new Dictionary<string, ApiType>(StringComparer.Ordinal);
        var newApi = new Dictionary<string, ApiType>(StringComparer.Ordinal);
        using (var blobs = new GitBlobReader(repoPath)) {
            foreach (var file in fileDiffs) {
                var (oldText, newText) = LoadContents(file, blobs);
                if (oldText is not null) ApiExtractor.Extract(oldText, file.OldPath!, oldApi);
                if (newText is not null) ApiExtractor.Extract(newText, file.NewPath!, newApi);
            }
        }

        var renamedFiles = fileDiffs
            .Where(f => !f.IsNew && !f.IsDeleted && f.OldPath != f.NewPath)
            .ToDictionary(f => f.OldPath!, f => f.NewPath!);

        Console.WriteLine($"Comparing {oldApi.Count} old types against {newApi.Count} new types");
        var report = ApiComparer.Compare(oldApi, newApi, renamedFiles);

        var existing = File.Exists(outputPath) ? File.ReadAllText(outputPath) : null;
        var markdown = MarkdownRenderer.Render(report, existing, commitArg);
        // Keep the line endings of the doc being replaced, otherwise use the platform default
        var newLine = existing is null ? Environment.NewLine : existing.Contains("\r\n") ? "\r\n" : "\n";
        if (newLine != "\n")
            markdown = markdown.Replace("\n", newLine);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, markdown);

        Console.WriteLine($"  {report.Types.Count(t => t.Breaking.Count > 0)} types with breaking changes");
        Console.WriteLine($"  {report.Types.Count(t => t.Changes.Count > 0)} types with additions");
        Console.WriteLine($"  {report.NewTypes.Count} new types, {report.Obsoletes.Count} types with new obsoletes");
        Console.WriteLine($"Wrote breaking changes doc to: {outputPath}");
    }

    private static (string? Old, string? New) LoadContents(FileDiff file, GitBlobReader blobs) {
        var oldText = file.IsNew ? null : blobs.Read(file.OldBlob);
        var newText = file.IsDeleted ? null : blobs.Read(file.NewBlob);

        // Fall back to reconstructing a missing side from the patch itself.
        if (file.IsNew && newText is null)
            newText = UnifiedDiff.Apply("", file.Hunks, reverse: false);
        else if (file.IsDeleted && oldText is null)
            oldText = UnifiedDiff.Apply("", file.Hunks, reverse: true);
        else if (oldText is null && newText is not null)
            oldText = UnifiedDiff.Apply(newText, file.Hunks, reverse: true);
        else if (newText is null && oldText is not null)
            newText = UnifiedDiff.Apply(oldText, file.Hunks, reverse: false);

        if (oldText is null && newText is null && !(file.IsNew || file.IsDeleted))
            Console.WriteLine($"  WARNING: could not load either side of {file.NewPath ?? file.OldPath}, skipping");
        return (oldText, newText);
    }

    #region Diff parsing

    private sealed record Hunk(int OldStart, int NewStart, List<string> Lines);

    private sealed class FileDiff {
        public string? OldPath;
        public string? NewPath;
        public string OldBlob = "";
        public string NewBlob = "";
        public bool IsNew;
        public bool IsDeleted;
        public List<Hunk> Hunks { get; } = [];
    }

    private static class UnifiedDiff {
        public static List<FileDiff> Parse(string diff) {
            var result = new List<FileDiff>();
            var lines = diff.Replace("\r", string.Empty).Split('\n');
            FileDiff? current = null;
            Hunk? hunk = null;
            int oldRemaining = 0, newRemaining = 0;

            foreach (var line in lines) {
                if (hunk is not null && (oldRemaining > 0 || newRemaining > 0)) {
                    hunk.Lines.Add(line);
                    if (line.StartsWith('\\')) continue;
                    if (line.StartsWith('-')) oldRemaining--;
                    else if (line.StartsWith('+')) newRemaining--;
                    else { oldRemaining--; newRemaining--; }
                    continue;
                }
                hunk = null;

                if (line.StartsWith("diff --git ", StringComparison.Ordinal)) {
                    current = new FileDiff();
                    result.Add(current);
                    var parts = line.Split(' ');
                    current.OldPath = parts[2][2..];
                    current.NewPath = parts[3][2..];
                } else if (current is null) {
                    continue;
                } else if (line.StartsWith("new file mode", StringComparison.Ordinal)) {
                    current.IsNew = true;
                } else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) {
                    current.IsDeleted = true;
                } else if (line.StartsWith("index ", StringComparison.Ordinal)) {
                    var hashes = line.Split(' ')[1].Split("..");
                    current.OldBlob = hashes[0];
                    current.NewBlob = hashes[1];
                } else if (HunkHeaderRegex.Match(line) is { Success: true } match) {
                    hunk = new Hunk(int.Parse(match.Groups["oldStart"].Value), int.Parse(match.Groups["newStart"].Value), []);
                    current.Hunks.Add(hunk);
                    oldRemaining = match.Groups["oldCount"].Success ? int.Parse(match.Groups["oldCount"].Value) : 1;
                    newRemaining = match.Groups["newCount"].Success ? int.Parse(match.Groups["newCount"].Value) : 1;
                }
            }

            return result;
        }

        public static string Apply(string source, List<Hunk> hunks, bool reverse) {
            var input = source.Length == 0 ? [] : source.Replace("\r", string.Empty).Split('\n');
            var output = new List<string>();
            var index = 0;
            var removed = reverse ? '+' : '-';
            var added = reverse ? '-' : '+';

            foreach (var hunk in hunks) {
                var start = Math.Max((reverse ? hunk.NewStart : hunk.OldStart) - 1, 0);
                while (index < start && index < input.Length)
                    output.Add(input[index++]);
                foreach (var line in hunk.Lines) {
                    if (line.StartsWith('\\')) continue;
                    if (line.Length > 0 && line[0] == removed) index++;
                    else if (line.Length > 0 && line[0] == added) output.Add(line[1..]);
                    else { output.Add(line.Length > 0 ? line[1..] : line); index++; }
                }
            }

            while (index < input.Length)
                output.Add(input[index++]);
            return string.Join('\n', output);
        }
    }

    private sealed class GitBlobReader : IDisposable {
        private readonly Process _process;
        private readonly Stream _stdout;
        private readonly StreamWriter _stdin;

        public GitBlobReader(string repoPath) {
            _process = Process.Start(new ProcessStartInfo {
                FileName = "git",
                Arguments = "cat-file --batch",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            }) ?? throw new InvalidOperationException("Could not start git");
            _stdout = _process.StandardOutput.BaseStream;
            _stdin = _process.StandardInput;
            _stdin.NewLine = "\n";
        }

        public string? Read(string hash) {
            if (string.IsNullOrEmpty(hash) || hash.All(c => c == '0'))
                return null;

            _stdin.Write(hash + "\n");
            _stdin.Flush();

            // "<sha> blob <size>" on success, "<hash> missing" / "<hash> ambiguous" otherwise
            var header = ReadLine().Split(' ');
            if (header.Length < 3 || header[1] != "blob")
                return null;

            var buffer = new byte[int.Parse(header[2])];
            _stdout.ReadExactly(buffer);
            _stdout.ReadByte(); // trailing LF
            return new UTF8Encoding(false).GetString(buffer).TrimStart('﻿');
        }

        private string ReadLine() {
            var bytes = new List<byte>();
            int b;
            while ((b = _stdout.ReadByte()) is not -1 and not '\n')
                bytes.Add((byte)b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public void Dispose() {
            _stdin.Close();
            _process.WaitForExit();
            _process.Dispose();
        }
    }

    #endregion

    #region API model

    private enum MemberKind {
        Field,
        FixedArray,
        Const,
        Property,
        Method,
        Delegate,
        Operator,
        EnumValue
    }

    private sealed class ApiType {
        public required string FullName { get; init; }
        public required string DisplayName { get; init; }
        public required string Namespace { get; init; }
        public required string Kind { get; set; }
        public string? ParentFullName { get; init; }
        public string? EnumUnderlying { get; set; }
        public string? Obsolete { get; set; }
        public List<string> Inherits { get; } = [];
        public List<string> Interfaces { get; } = [];
        public List<ApiMember> Members { get; } = [];
        public HashSet<string> Files { get; } = [];

        public string SimpleName => DisplayName[(DisplayName.LastIndexOf('.') + 1)..];
    }

    private sealed class ApiMember {
        public required MemberKind Kind { get; init; }
        public required string Name { get; init; }
        public required string Type { get; init; }
        public required bool IsPublic { get; init; }
        public bool IsStatic { get; init; }
        public List<string> Parameters { get; init; } = [];
        public long? Offset { get; init; }
        public string? Hint { get; init; }
        public string? Obsolete { get; init; }
        public long? Value { get; init; }

        public bool IsCallable => Kind is MemberKind.Method or MemberKind.Delegate or MemberKind.Operator;
        public bool IsData => Kind is MemberKind.Field or MemberKind.FixedArray or MemberKind.Const or MemberKind.Property;
        public string Key => Kind is MemberKind.Method or MemberKind.Delegate ? $"{Name}({string.Join(", ", Parameters)})" : Name;
    }

    private static class ApiExtractor {
        private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

        public static void Extract(string source, string path, Dictionary<string, ApiType> types) {
            var root = CSharpSyntaxTree.ParseText(source, ParseOptions).GetCompilationUnitRoot();
            Visit(root, "", null, new FileContext(path, types));
        }

        private sealed record FileContext(string Path, Dictionary<string, ApiType> Types);

        private static void Visit(SyntaxNode node, string ns, ApiType? parent, FileContext context) {
            foreach (var child in node.ChildNodes()) {
                switch (child) {
                    case BaseNamespaceDeclarationSyntax nsDecl:
                        Visit(nsDecl, ns.Length == 0 ? nsDecl.Name.ToString() : $"{ns}.{nsDecl.Name}", parent, context);
                        break;
                    case BaseTypeDeclarationSyntax typeDecl when IsPublic(typeDecl.Modifiers, parent):
                        VisitType(typeDecl, ns, parent, context);
                        break;
                    case DelegateDeclarationSyntax delegateDecl when parent is null && IsPublic(delegateDecl.Modifiers, null):
                        // Top level delegates are tracked as a type with a single "Invoke" member.
                        var delegateType = GetOrCreateType(delegateDecl.Identifier.Text, "delegate", ns, null, context);
                        delegateType.Members.Add(CreateCallable(MemberKind.Delegate, "Invoke", delegateDecl.ReturnType, delegateDecl.ParameterList, false, null, delegateDecl.AttributeLists));
                        break;
                }
            }
        }

        private static void VisitType(BaseTypeDeclarationSyntax decl, string ns, ApiType? parent, FileContext context) {
            var type = GetOrCreateType(decl, ns, parent, context);
            if (decl is EnumDeclarationSyntax enumDecl)
                ExtractEnum(enumDecl, type);
            else
                ExtractMembers((TypeDeclarationSyntax)decl, type, ns, context);
        }

        private static ApiType GetOrCreateType(BaseTypeDeclarationSyntax decl, string ns, ApiType? parent, FileContext context) {
            var name = decl.Identifier.Text;
            if (decl is TypeDeclarationSyntax { TypeParameterList: { } typeParams })
                name += $"<{string.Join(", ", typeParams.Parameters.Select(p => p.Identifier.Text))}>";

            var kind = decl switch {
                RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
                TypeDeclarationSyntax t => t.Keyword.Text,
                EnumDeclarationSyntax e => e.EnumKeyword.Text,
                _ => "type"
            };

            var type = GetOrCreateType(name, kind, ns, parent, context);

            if (GetObsolete(decl.AttributeLists) is { } obsolete)
                type.Obsolete = obsolete;

            foreach (var attribute in decl.AttributeLists.SelectMany(l => l.Attributes)) {
                if (AttributeName(attribute) == "Inherits" && attribute.Name is GenericNameSyntax generic)
                    type.Inherits.Add(Normalize(generic.TypeArgumentList.Arguments[0]));
            }

            if (decl.BaseList is { } baseList) {
                foreach (var baseType in baseList.Types) {
                    var text = Normalize(baseType.Type);
                    if (decl is EnumDeclarationSyntax)
                        type.EnumUnderlying = text;
                    else if (!type.Interfaces.Contains(text))
                        type.Interfaces.Add(text);
                }
            }

            return type;
        }

        private static ApiType GetOrCreateType(string name, string kind, string ns, ApiType? parent, FileContext context) {
            var displayName = parent is null ? name : $"{parent.DisplayName}.{name}";
            var fullName = ns.Length == 0 ? displayName : $"{ns}.{displayName}";
            if (!context.Types.TryGetValue(fullName, out var type)) {
                type = new ApiType {
                    FullName = fullName,
                    DisplayName = displayName,
                    Namespace = ns,
                    Kind = kind,
                    ParentFullName = parent?.FullName
                };
                context.Types[fullName] = type;
            }
            type.Files.Add(context.Path);
            return type;
        }

        private static void ExtractEnum(EnumDeclarationSyntax decl, ApiType type) {
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            long? next = 0;
            foreach (var member in decl.Members) {
                var value = member.EqualsValue is { } equals ? Evaluate(equals.Value, values) : next;
                if (value is { } v)
                    values[member.Identifier.Text] = v;
                next = value + 1;

                type.Members.Add(new ApiMember {
                    Kind = MemberKind.EnumValue,
                    Name = member.Identifier.Text,
                    Type = member.EqualsValue?.Value.ToString() ?? value?.ToString(CultureInfo.InvariantCulture) ?? "?",
                    IsPublic = true,
                    Value = value,
                    Obsolete = GetObsolete(member.AttributeLists)
                });
            }
        }

        private static void ExtractMembers(TypeDeclarationSyntax decl, ApiType type, string ns, FileContext context) {
            var isInterface = decl is InterfaceDeclarationSyntax;
            foreach (var member in decl.Members) {
                var isPublic = isInterface ? !member.Modifiers.Any(SyntaxKind.PrivateKeyword) : IsPublic(member.Modifiers, type);
                var obsolete = GetObsolete(member.AttributeLists);

                switch (member) {
                    case BaseTypeDeclarationSyntax nested when isPublic:
                        VisitType(nested, ns, type, context);
                        break;
                    case FieldDeclarationSyntax field:
                        ExtractField(field, type, isPublic, obsolete);
                        break;
                    case PropertyDeclarationSyntax property when isPublic:
                        type.Members.Add(new ApiMember { Kind = MemberKind.Property, Name = property.Identifier.Text, Type = Normalize(property.Type), IsPublic = true, Obsolete = obsolete });
                        break;
                    case IndexerDeclarationSyntax indexer when isPublic:
                        type.Members.Add(new ApiMember {
                            Kind = MemberKind.Property,
                            Name = $"this[{string.Join(", ", indexer.ParameterList.Parameters.Select(FormatParameter))}]",
                            Type = Normalize(indexer.Type),
                            IsPublic = true,
                            Obsolete = obsolete
                        });
                        break;
                    case MethodDeclarationSyntax method when isPublic:
                        var methodName = method.Identifier.Text + (method.TypeParameterList is { } tp ? $"<{string.Join(", ", tp.Parameters.Select(p => p.Identifier.Text))}>" : "");
                        type.Members.Add(CreateCallable(MemberKind.Method, methodName, method.ReturnType, method.ParameterList, method.Modifiers.Any(SyntaxKind.StaticKeyword), obsolete, method.AttributeLists));
                        break;
                    case ConstructorDeclarationSyntax ctor when isPublic && !ctor.Modifiers.Any(SyntaxKind.StaticKeyword):
                        type.Members.Add(CreateCallable(MemberKind.Method, decl.Identifier.Text, null, ctor.ParameterList, false, obsolete, ctor.AttributeLists));
                        break;
                    case DelegateDeclarationSyntax del when isPublic:
                        type.Members.Add(CreateCallable(MemberKind.Delegate, del.Identifier.Text, del.ReturnType, del.ParameterList, false, obsolete, del.AttributeLists));
                        break;
                    case ConversionOperatorDeclarationSyntax conversion when isPublic:
                        type.Members.Add(new ApiMember {
                            Kind = MemberKind.Operator,
                            Name = $"operator {Normalize(conversion.Type)}",
                            Type = conversion.ImplicitOrExplicitKeyword.Text,
                            IsPublic = true,
                            Obsolete = obsolete
                        });
                        break;
                    case OperatorDeclarationSyntax op when isPublic:
                        type.Members.Add(new ApiMember {
                            Kind = MemberKind.Operator,
                            Name = $"operator {op.OperatorToken.Text}({string.Join(", ", op.ParameterList.Parameters.Select(FormatParameter))})",
                            Type = Normalize(op.ReturnType),
                            IsPublic = true,
                            Obsolete = obsolete
                        });
                        break;
                }
            }
        }

        private static void ExtractField(FieldDeclarationSyntax field, ApiType type, bool isPublic, string? obsolete) {
            var attributes = field.AttributeLists.SelectMany(l => l.Attributes).ToList();
            long? offset = null;
            var isFixedArray = false;

            foreach (var attribute in attributes) {
                switch (AttributeName(attribute)) {
                    case "FieldOffset" when attribute.ArgumentList?.Arguments.Count > 0:
                        offset = Evaluate(attribute.ArgumentList.Arguments[0].Expression, []);
                        break;
                    case "FixedSizeArray":
                        isFixedArray = true;
                        break;
                    case "BitField" when attribute is { Name: GenericNameSyntax generic, ArgumentList.Arguments.Count: > 0 }:
                        // [BitField<bool>(nameof(IsCasting), 0)] generates a public property on top of the field
                        var nameArg = attribute.ArgumentList.Arguments[0].Expression;
                        var propName = nameArg switch {
                            InvocationExpressionSyntax { ArgumentList.Arguments: [var arg] } => arg.Expression.ToString(),
                            LiteralExpressionSyntax literal => literal.Token.ValueText,
                            _ => nameArg.ToString()
                        };
                        type.Members.Add(new ApiMember { Kind = MemberKind.Property, Name = propName, Type = Normalize(generic.TypeArgumentList.Arguments[0]), IsPublic = true, Obsolete = obsolete });
                        break;
                }
            }

            var isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
            var fieldType = Normalize(field.Declaration.Type);
            if (isFixedArray && field.Declaration.Type is GenericNameSyntax fixedArray)
                fieldType = Normalize(fixedArray.TypeArgumentList.Arguments[0]);

            foreach (var variable in field.Declaration.Variables) {
                type.Members.Add(new ApiMember {
                    Kind = isConst ? MemberKind.Const : isFixedArray ? MemberKind.FixedArray : MemberKind.Field,
                    Name = variable.Identifier.Text,
                    Type = fieldType,
                    // Fixed size arrays are internal but are exposed through a generated public property
                    IsPublic = isPublic || isFixedArray,
                    IsStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword) && !isConst,
                    Offset = offset,
                    Obsolete = obsolete
                });
            }
        }

        private static ApiMember CreateCallable(MemberKind kind, string name, TypeSyntax? returnType, ParameterListSyntax parameters, bool isStatic, string? obsolete, SyntaxList<AttributeListSyntax> attributeLists) {
            string? hint = null;
            foreach (var attribute in attributeLists.SelectMany(l => l.Attributes)) {
                var attributeName = AttributeName(attribute);
                if (attributeName is "MemberFunction" or "VirtualFunction" && attribute.ArgumentList?.Arguments.Count > 0)
                    hint = $"{attributeName}:{attribute.ArgumentList.Arguments[0].Expression}";
            }

            return new ApiMember {
                Kind = kind,
                Name = name,
                Type = returnType is null ? "" : Normalize(returnType),
                IsPublic = true,
                IsStatic = isStatic,
                Parameters = parameters.Parameters.Select(FormatParameter).ToList(),
                Hint = hint,
                Obsolete = obsolete
            };
        }

        private static string FormatParameter(ParameterSyntax parameter) {
            var modifiers = parameter.Modifiers
                .Where(m => !m.IsKind(SyntaxKind.ScopedKeyword) && !m.IsKind(SyntaxKind.ThisKeyword))
                .Select(m => m.Text);
            return string.Join(' ', modifiers.Append(parameter.Type is null ? "" : Normalize(parameter.Type)));
        }

        private static bool IsPublic(SyntaxTokenList modifiers, ApiType? parent) {
            if (parent is { Kind: "interface" })
                return !modifiers.Any(SyntaxKind.PrivateKeyword);
            if (modifiers.Any(SyntaxKind.PublicKeyword))
                return true;
            return modifiers.Any(SyntaxKind.ProtectedKeyword) && !modifiers.Any(SyntaxKind.PrivateKeyword);
        }

        private static string? GetObsolete(SyntaxList<AttributeListSyntax> attributeLists) {
            var attribute = attributeLists.SelectMany(l => l.Attributes).FirstOrDefault(a => AttributeName(a) == "Obsolete");
            if (attribute is null) return null;
            return attribute.ArgumentList is null ? "" : Normalize(attribute.ArgumentList);
        }

        private static string AttributeName(AttributeSyntax attribute) {
            var name = attribute.Name switch {
                QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                SimpleNameSyntax simple => simple.Identifier.Text,
                _ => attribute.Name.ToString()
            };
            return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
        }

        private static long? Evaluate(ExpressionSyntax expression, Dictionary<string, long> known) {
            try {
                return expression switch {
                    LiteralExpressionSyntax literal => literal.Token.Value switch {
                        ulong u => unchecked((long)u),
                        char c => c,
                        IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
                        _ => null
                    },
                    ParenthesizedExpressionSyntax paren => Evaluate(paren.Expression, known),
                    CastExpressionSyntax cast => Evaluate(cast.Expression, known),
                    CheckedExpressionSyntax check => Evaluate(check.Expression, known),
                    IdentifierNameSyntax identifier => known.TryGetValue(identifier.Identifier.Text, out var v) ? v : null,
                    MemberAccessExpressionSyntax access => known.TryGetValue(access.Name.Identifier.Text, out var v) ? v : null,
                    PrefixUnaryExpressionSyntax unary => Evaluate(unary.Operand, known) is { } operand
                        ? unary.Kind() switch {
                            SyntaxKind.UnaryMinusExpression => -operand,
                            SyntaxKind.UnaryPlusExpression => operand,
                            SyntaxKind.BitwiseNotExpression => ~operand,
                            _ => null
                        }
                        : null,
                    BinaryExpressionSyntax binary => Evaluate(binary.Left, known) is { } left && Evaluate(binary.Right, known) is { } right
                        ? binary.Kind() switch {
                            SyntaxKind.LeftShiftExpression => left << (int)right,
                            SyntaxKind.RightShiftExpression => left >> (int)right,
                            SyntaxKind.BitwiseOrExpression => left | right,
                            SyntaxKind.BitwiseAndExpression => left & right,
                            SyntaxKind.ExclusiveOrExpression => left ^ right,
                            SyntaxKind.AddExpression => left + right,
                            SyntaxKind.SubtractExpression => left - right,
                            SyntaxKind.MultiplyExpression => left * right,
                            _ => null
                        }
                        : null,
                    _ => null
                };
            } catch (Exception e) when (e is OverflowException or InvalidCastException) {
                return null;
            }
        }

        private static string Normalize(SyntaxNode node) => node.NormalizeWhitespace().ToString();
    }

    #endregion

    #region Comparison

    private sealed record Bullet(string Text, List<Bullet> Children) {
        public Bullet(string text) : this(text, []) { }
    }

    private sealed class TypeReport(string displayName) {
        public string DisplayName { get; } = displayName;
        public List<Bullet> Breaking { get; } = [];
        public List<Bullet> Changes { get; } = [];
    }

    private sealed class Report {
        public List<TypeReport> Types { get; } = [];
        public List<string> NewTypes { get; } = [];
        public List<TypeReport> Obsoletes { get; } = [];
    }

    private static class ApiComparer {
        private static readonly Regex DigitsRegex = new(@"\d+", RegexOptions.Compiled);

        public static Report Compare(Dictionary<string, ApiType> oldApi, Dictionary<string, ApiType> newApi, Dictionary<string, string> renamedFiles) {
            var report = new Report();
            var pairs = MatchTypes(oldApi, newApi, renamedFiles, out var removed, out var added);

            foreach (var type in removed) {
                // A nested type disappearing alongside its parent is covered by the parent being removed
                if (type.ParentFullName is { } parent && removed.Any(t => t.FullName == parent))
                    continue;
                var typeReport = new TypeReport(type.DisplayName);
                typeReport.Breaking.Add(new Bullet(type.Obsolete is not null ? "Removed (was obsolete)" : "Removed"));
                report.Types.Add(typeReport);
            }

            foreach (var (oldType, newType, note) in pairs) {
                var typeReport = new TypeReport(oldType.DisplayName);
                if (note is not null)
                    typeReport.Breaking.Add(note);
                CompareType(oldType, newType, typeReport);
                if (typeReport.Breaking.Count > 0 || typeReport.Changes.Count > 0)
                    report.Types.Add(typeReport);

                var obsoleteReport = new TypeReport(newType.DisplayName);
                if (newType.Obsolete is not null && oldType.Obsolete != newType.Obsolete)
                    obsoleteReport.Breaking.Add(new Bullet("Entire type"));
                foreach (var member in newType.Members.Where(m => m.IsPublic && m.Obsolete is not null)) {
                    var oldMember = oldType.Members.FirstOrDefault(m => m.Key == member.Key && m.Kind == member.Kind);
                    if (oldMember?.Obsolete != member.Obsolete)
                        obsoleteReport.Breaking.Add(new Bullet(Code(member.Key)));
                }
                if (obsoleteReport.Breaking.Count > 0)
                    report.Obsoletes.Add(obsoleteReport);
            }

            foreach (var type in added) {
                if (type.ParentFullName is { } parent && added.Any(t => t.FullName == parent))
                    continue;
                report.NewTypes.Add(type.DisplayName);
            }

            return report;
        }

        private static List<(ApiType Old, ApiType New, Bullet? Note)> MatchTypes(Dictionary<string, ApiType> oldApi, Dictionary<string, ApiType> newApi, Dictionary<string, string> renamedFiles, out List<ApiType> removed, out List<ApiType> added) {
            var pairs = new List<(ApiType, ApiType, Bullet?)>();
            var unmatchedOld = new List<ApiType>();
            var unmatchedNew = newApi.Values.Where(t => !oldApi.ContainsKey(t.FullName)).ToList();

            foreach (var oldType in oldApi.Values) {
                if (newApi.TryGetValue(oldType.FullName, out var newType))
                    pairs.Add((oldType, newType, null));
                else
                    unmatchedOld.Add(oldType);
            }

            // Same nested name, different namespace
            foreach (var oldType in unmatchedOld.ToList()) {
                var newType = unmatchedNew.FirstOrDefault(t => t.DisplayName == oldType.DisplayName && t.Kind == oldType.Kind);
                if (newType is null) continue;
                pairs.Add((oldType, newType, new Bullet($"Moved from namespace {Code(oldType.Namespace)} to {Code(newType.Namespace)}")));
                unmatchedOld.Remove(oldType);
                unmatchedNew.Remove(newType);
            }

            // Same name, moved in or out of nesting
            foreach (var oldType in unmatchedOld.ToList()) {
                var newType = unmatchedNew.FirstOrDefault(t => t.SimpleName == oldType.SimpleName && t.Kind == oldType.Kind);
                if (newType is null) continue;
                pairs.Add((oldType, newType, new Bullet($"Moved to {Code(newType.DisplayName)}")));
                unmatchedOld.Remove(oldType);
                unmatchedNew.Remove(newType);
            }

            // Renames where git also tracked the file as renamed and it holds a single unmatched type on each side
            foreach (var (oldPath, newPath) in renamedFiles) {
                var oldTypes = unmatchedOld.Where(t => t.ParentFullName is null && t.Files.Contains(oldPath)).ToList();
                var newTypes = unmatchedNew.Where(t => t.ParentFullName is null && t.Files.Contains(newPath)).ToList();
                if (oldTypes is not [var oldType] || newTypes is not [var newType] || oldType.Kind != newType.Kind) continue;
                pairs.Add((oldType, newType, new Bullet($"Renamed to {Code(newType.DisplayName)}")));
                unmatchedOld.Remove(oldType);
                unmatchedNew.Remove(newType);
            }

            // Renames, detected by members sharing names
            var candidates = (
                from oldType in unmatchedOld
                from newType in unmatchedNew
                where oldType.Kind == newType.Kind
                let score = Similarity(oldType, newType)
                where score >= 0.5
                orderby score descending
                select (oldType, newType)).ToList();
            foreach (var (oldType, newType) in candidates) {
                if (!unmatchedOld.Contains(oldType) || !unmatchedNew.Contains(newType)) continue;
                pairs.Add((oldType, newType, new Bullet($"Renamed to {Code(newType.DisplayName)}")));
                unmatchedOld.Remove(oldType);
                unmatchedNew.Remove(newType);
            }

            removed = unmatchedOld.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            added = unmatchedNew.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            return pairs;
        }

        private static double Similarity(ApiType a, ApiType b) {
            var aNames = a.Members.Where(m => m.IsPublic).Select(m => m.Name).ToHashSet();
            var bNames = b.Members.Where(m => m.IsPublic).Select(m => m.Name).ToHashSet();
            var shared = aNames.Intersect(bNames).Count();
            if (shared < 2) return 0;
            return (double)shared / aNames.Union(bNames).Count();
        }

        private static void CompareType(ApiType oldType, ApiType newType, TypeReport report) {
            if (oldType.Kind != newType.Kind)
                report.Breaking.Add(new Bullet($"Changed from {Code(oldType.Kind)} to {Code(newType.Kind)}"));

            if (oldType.Kind == "enum" && newType.Kind == "enum") {
                var oldUnderlying = oldType.EnumUnderlying ?? "int";
                var newUnderlying = newType.EnumUnderlying ?? "int";
                if (oldUnderlying != newUnderlying)
                    report.Breaking.Add(new Bullet($"Changed underlying type from {Code(oldUnderlying)} to {Code(newUnderlying)}"));
            }

            // An embedded base struct field replaced by [Inherits<T>]
            var replacedByInheritance = oldType.Members
                .Where(m => m is { Kind: MemberKind.Field, IsPublic: true } && newType.Inherits.Contains(m.Type) && !oldType.Inherits.Contains(m.Type))
                .Where(m => !newType.Members.Any(n => n.IsData && n.Name == m.Name))
                .ToList();
            foreach (var field in replacedByInheritance)
                report.Breaking.Add(new Bullet($"Changed inheritance to include {Code(field.Type)}, replacing field {Code(field.Name)}"));

            var newInherits = newType.Inherits.Where(i => oldType.Inherits.Contains(i) || replacedByInheritance.All(f => f.Type != i)).ToList();
            if (!oldType.Inherits.SequenceEqual(newInherits)) {
                if (oldType.Inherits.Count == 0)
                    report.Changes.Add(new Bullet($"Now inherits {CodeList(newInherits)}"));
                else if (newInherits.Count == 0)
                    report.Breaking.Add(new Bullet($"Removed inheritance from {CodeList(oldType.Inherits)}"));
                else if (newInherits.Take(oldType.Inherits.Count).SequenceEqual(oldType.Inherits))
                    report.Breaking.Add(new Bullet($"Changed inheritance to include {CodeList(newInherits.Skip(oldType.Inherits.Count))}"));
                else
                    report.Breaking.Add(new Bullet($"Changed inheritance chain from {CodeList(oldType.Inherits)} to {CodeList(newInherits)}"));
            }

            foreach (var oldInterface in oldType.Interfaces.Where(i => !newType.Interfaces.Contains(i))) {
                var stem = InterfaceStem(oldInterface);
                var replacement = newType.Interfaces.FirstOrDefault(i => !oldType.Interfaces.Contains(i) && InterfaceStem(i) == stem);
                report.Breaking.Add(new Bullet(replacement is null
                    ? $"Removed {Code(oldInterface)}"
                    : $"Changed {Code(oldInterface)} to {Code(replacement)}"));
            }
            foreach (var newInterface in newType.Interfaces.Where(i => !oldType.Interfaces.Contains(i))) {
                var stem = InterfaceStem(newInterface);
                if (!oldType.Interfaces.Any(i => InterfaceStem(i) == stem))
                    report.Changes.Add(new Bullet($"Now implements {Code(newInterface)}"));
            }

            if (newType.Kind == "enum")
                CompareEnumValues(oldType, newType, report);
            else {
                CompareDataMembers(oldType, newType, report, replacedByInheritance);
                CompareCallables(oldType, newType, report);
            }
        }

        private static void CompareEnumValues(ApiType oldType, ApiType newType, TypeReport report) {
            var oldValues = oldType.Members.Where(m => m.Kind == MemberKind.EnumValue).ToList();
            var newValues = newType.Members.Where(m => m.Kind == MemberKind.EnumValue).ToList();
            var oldNames = oldValues.Select(m => m.Name).ToHashSet();
            var newByName = newValues.ToDictionary(m => m.Name);
            var addedValues = newValues.Where(m => !oldNames.Contains(m.Name)).ToList();

            var changed = new List<(ApiMember Old, ApiMember New)>();
            foreach (var oldValue in oldValues) {
                if (newByName.TryGetValue(oldValue.Name, out var newValue)) {
                    if (oldValue.Value != newValue.Value || (oldValue.Value is null && oldValue.Type != newValue.Type))
                        changed.Add((oldValue, newValue));
                    continue;
                }

                var renamed = addedValues.FirstOrDefault(m => m.Value is not null && m.Value == oldValue.Value);
                if (renamed is not null) {
                    addedValues.Remove(renamed);
                    report.Breaking.Add(new Bullet($"{Code(oldValue.Name)} renamed to {Code(renamed.Name)}"));
                } else {
                    report.Breaking.Add(new Bullet($"{Code(oldValue.Name)} removed"));
                }
            }

            if (changed.Count >= 3) {
                var firstIndex = oldValues.IndexOf(changed[0].Old);
                report.Breaking.Add(firstIndex > 0 && oldValues[firstIndex - 1].Value is { } previous
                    ? new Bullet($"Values shifted after {Code(previous.ToString(CultureInfo.InvariantCulture))}")
                    : new Bullet("Values shifted"));
            } else {
                foreach (var (oldValue, newValue) in changed)
                    report.Breaking.Add(new Bullet($"{Code(oldValue.Name)} changed value from {Code(FormatValue(oldValue))} to {Code(FormatValue(newValue))}"));
            }

            AddAdditions(report, addedValues);
        }

        private static void CompareDataMembers(ApiType oldType, ApiType newType, TypeReport report, List<ApiMember> alreadyReported) {
            var oldMembers = oldType.Members.Where(m => m.IsData && m.IsPublic && !alreadyReported.Contains(m)).DistinctBy(m => m.Name).ToList();
            var newMembers = newType.Members.Where(m => m.IsData).DistinctBy(m => m.Name).ToList();
            var oldNames = oldType.Members.Where(m => m.IsData).Select(m => m.Name).ToHashSet();
            var unmatchedNew = newMembers.Where(m => m.IsPublic && !oldNames.Contains(m.Name)).ToList();
            var newCallableNames = newType.Members.Where(m => m.IsCallable).Select(m => m.Name).ToHashSet();

            foreach (var oldMember in oldMembers) {
                var newMember = newMembers.FirstOrDefault(m => m.Name == oldMember.Name);
                if (newMember is not null) {
                    if (!newMember.IsPublic) {
                        report.Breaking.Add(new Bullet($"{Code(oldMember.Name)} is no longer public"));
                    } else if (KindName(oldMember.Kind) != KindName(newMember.Kind)) {
                        var bullet = new Bullet($"{Code(oldMember.Name)} changed from {KindName(oldMember.Kind)} to {KindName(newMember.Kind)}");
                        if (oldMember.Type != newMember.Type)
                            bullet.Children.Add(new Bullet($"Changed type from {Code(oldMember.Type)} to {Code(newMember.Type)}"));
                        report.Breaking.Add(bullet);
                    } else if (oldMember.Type != newMember.Type) {
                        var what = oldMember.Kind == MemberKind.FixedArray ? "array type" : oldMember.Kind == MemberKind.Const ? "value" : "type";
                        report.Breaking.Add(new Bullet($"{Code(oldMember.Name)} changed {what} from {Code(oldMember.Type)} to {Code(newMember.Type)}"));
                    }
                    if (newMember.IsPublic && oldMember.IsStatic != newMember.IsStatic)
                        report.Breaking.Add(new Bullet(StaticChange(oldMember.Name, newMember.IsStatic)));
                    continue;
                }

                // A data member turned into a method/delegate of the same name
                if (newCallableNames.Contains(oldMember.Name)) {
                    report.Breaking.Add(new Bullet($"{Code(oldMember.Name)} changed from {KindName(oldMember.Kind)} to method"));
                    continue;
                }

                // A FixedSizeArray turned into a plain field (or back) keeps the name of its generated span property
                var replaced = unmatchedNew.FirstOrDefault(m =>
                    (oldMember.Kind == MemberKind.FixedArray && m.Kind != MemberKind.FixedArray && m.Name == GeneratedPropertyName(oldMember.Name)) ||
                    (oldMember.Kind != MemberKind.FixedArray && m.Kind == MemberKind.FixedArray && GeneratedPropertyName(m.Name) == oldMember.Name));
                if (replaced is not null) {
                    unmatchedNew.Remove(replaced);
                    var name = oldMember.Kind == MemberKind.FixedArray ? replaced.Name : oldMember.Name;
                    report.Breaking.Add(new Bullet($"{Code(name)} changed type from {Code(SpanType(oldMember))} to {Code(SpanType(replaced))}"));
                    continue;
                }

                // Renamed fields usually keep their offset, prefer a candidate with the same type for unions.
                // Fields named after their offset (Flags618 -> Flags628) move with it, so match those by name shape instead.
                var renamed = unmatchedNew.FirstOrDefault(m =>
                        m.Kind == oldMember.Kind && m.Type == oldMember.Type &&
                        DigitsRegex.Replace(m.Name, "#") == DigitsRegex.Replace(oldMember.Name, "#") && DigitsRegex.IsMatch(oldMember.Name))
                    ?? (oldMember.Offset is null ? null : unmatchedNew
                        .Where(m => m.Offset == oldMember.Offset && KindName(m.Kind) == KindName(oldMember.Kind))
                        .OrderBy(m => m.Type == oldMember.Type ? 0 : 1)
                        .FirstOrDefault());
                if (renamed is not null) {
                    unmatchedNew.Remove(renamed);
                    var bullet = new Bullet($"{Code(oldMember.Name)} renamed to {Code(renamed.Name)}");
                    if (renamed.Type != oldMember.Type)
                        bullet.Children.Add(new Bullet($"Changed type from {Code(oldMember.Type)} to {Code(renamed.Type)}"));
                    report.Breaking.Add(bullet);
                    continue;
                }

                report.Breaking.Add(new Bullet($"{Code(oldMember.Name)} removed{(oldMember.Obsolete is not null ? " (was obsolete)" : "")}"));
            }

            AddAdditions(report, unmatchedNew);
        }

        private static void CompareCallables(ApiType oldType, ApiType newType, TypeReport report) {
            var oldMembers = oldType.Members.Where(m => m.IsCallable && m.IsPublic).DistinctBy(m => m.Key).ToList();
            var newMembers = newType.Members.Where(m => m.IsCallable && m.IsPublic).DistinctBy(m => m.Key).ToList();
            var newByKey = newMembers.ToDictionary(m => m.Key);
            var oldKeys = oldMembers.Select(m => m.Key).ToHashSet();
            var unmatchedNew = newMembers.Where(m => !oldKeys.Contains(m.Key)).ToList();
            var newDataNames = newType.Members.Where(m => m.IsData && m.IsPublic).Select(m => m.Name).ToHashSet();
            var unmatchedOld = new List<ApiMember>();

            foreach (var oldMember in oldMembers) {
                if (!newByKey.TryGetValue(oldMember.Key, out var newMember)) {
                    unmatchedOld.Add(oldMember);
                    continue;
                }

                if (oldMember.IsStatic != newMember.IsStatic)
                    report.Breaking.Add(new Bullet(StaticChange(oldMember.Key, newMember.IsStatic)));

                if (oldMember.Type == newMember.Type)
                    continue;

                if (oldMember.Kind == MemberKind.Operator)
                    report.Breaking.Add(new Bullet($"{Code($"{oldMember.Type} {oldMember.Name}")} changed to {newMember.Type}"));
                else if (CtorDtorRegex.IsMatch(oldMember.Name) && oldMember.Type == "void")
                    report.Breaking.Add(new Bullet(Code(oldMember.Name)));
                else
                    report.Breaking.Add(new Bullet($"{Code(oldMember.Key)} changed return type from {Code(oldMember.Type)} to {Code(newMember.Type)}"));
            }

            foreach (var oldMember in unmatchedOld) {
                // Same name with different parameters: prefer the overload with the same parameter count
                var replacement = unmatchedNew
                    .Where(m => m.Name == oldMember.Name && m.Kind == oldMember.Kind)
                    .OrderBy(m => Math.Abs(m.Parameters.Count - oldMember.Parameters.Count))
                    .FirstOrDefault();
                var verb = "changed signature to";

                // Different name, same signature or vtable index
                if (replacement is null && oldMember.Hint is not null) {
                    replacement = unmatchedNew.FirstOrDefault(m => m.Hint == oldMember.Hint && m.Kind == oldMember.Kind);
                    verb = "renamed to";
                }

                if (replacement is null) {
                    var text = newDataNames.Contains(oldMember.Name)
                        ? $"{Code(oldMember.Key)} changed from method to {KindName(newType.Members.First(m => m.Name == oldMember.Name && m.IsData).Kind)}"
                        : $"{Code(oldMember.Key)} removed{(oldMember.Obsolete is not null ? " (was obsolete)" : "")}";
                    report.Breaking.Add(new Bullet(text));
                    continue;
                }

                unmatchedNew.Remove(replacement);
                if (oldMember.Type == replacement.Type) {
                    report.Breaking.Add(new Bullet($"{Code(oldMember.Key)} {verb} {Code(replacement.Key)}"));
                } else {
                    report.Breaking.Add(new Bullet(Code(oldMember.Key), [
                        new Bullet($"{char.ToUpperInvariant(verb[0])}{verb[1..]} {Code(replacement.Key)}"),
                        new Bullet($"Changed return type from {Code(oldMember.Type)} to {Code(replacement.Type)}")
                    ]));
                }
            }

            AddAdditions(report, unmatchedNew);
        }

        private static void AddAdditions(TypeReport report, List<ApiMember> added) {
            var groups = new (string Singular, string Plural, List<string> Names)[] {
                ("field", "fields", []),
                ("property", "properties", []),
                ("method", "methods", []),
                ("delegate", "delegates", []),
                ("operator", "operators", []),
                ("value", "values", []),
            };

            foreach (var member in added) {
                switch (member.Kind) {
                    case MemberKind.Field or MemberKind.Const:
                        groups[0].Names.Add(member.Name);
                        break;
                    case MemberKind.FixedArray:
                        groups[0].Names.Add(member.Name);
                        groups[1].Names.Add(GeneratedPropertyName(member.Name));
                        break;
                    case MemberKind.Property: groups[1].Names.Add(member.Name); break;
                    case MemberKind.Method: groups[2].Names.Add(member.Key); break;
                    case MemberKind.Delegate: groups[3].Names.Add(member.Key); break;
                    case MemberKind.Operator: groups[4].Names.Add(member.Kind == MemberKind.Operator && member.Type is "implicit" or "explicit" ? $"{member.Type} {member.Name}" : member.Name); break;
                    case MemberKind.EnumValue: groups[5].Names.Add(member.Name); break;
                }
            }

            foreach (var (singular, plural, names) in groups) {
                var distinct = names.Distinct().ToList();
                if (distinct.Count == 1)
                    report.Changes.Add(new Bullet($"Added new {singular} {Code(distinct[0])}"));
                else if (distinct.Count > 1)
                    report.Changes.Add(new Bullet($"Added new {plural}", distinct.Select(n => new Bullet(Code(n))).ToList()));
            }
        }

        private static string KindName(MemberKind kind) => kind switch {
            MemberKind.Field or MemberKind.FixedArray => "field",
            MemberKind.Const => "constant",
            MemberKind.Property => "property",
            _ => "method"
        };

        /// <summary>FixedSizeArray fields generate a span property named after the field, e.g. _unlockedItems -> UnlockedItems.</summary>
        private static string GeneratedPropertyName(string fieldName) {
            var trimmed = fieldName.TrimStart('_');
            return trimmed.Length == 0 ? fieldName : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        }

        private static string SpanType(ApiMember member) => member.Kind == MemberKind.FixedArray ? $"Span<{member.Type}>" : member.Type;

        private static string StaticChange(string name, bool isStatic) =>
            isStatic ? $"{Code(name)} changed from instance to static member" : $"{Code(name)} changed from static to instance member";

        private static string FormatValue(ApiMember member) => member.Value?.ToString(CultureInfo.InvariantCulture) ?? member.Type;

        private static string InterfaceStem(string name) => name.Split('<')[0];

        private static string CodeList(IEnumerable<string> names) => string.Join(", ", names.Select(Code));
    }

    private static string Code(string text) => text.Contains('`') ? $"`` {text} ``" : $"`{text}`";

    #endregion

    #region Markdown

    private static class MarkdownRenderer {
        private const string BreakingHeading = "## Breaking Changes";
        private const string ListHeading = "### List of breaking changes";
        private const string ChangesHeading = "## Changes and Additions";
        private const string NewTypesHeading = "## New Structs and Enums";
        private const string ObsoleteHeading = "## Obsolete";
        private const string OtherNotesHeading = "## Other notes";

        public static string Render(Report report, string? existing, string? commit) {
            var preserved = Preserved.From(existing);
            var sb = new StringBuilder();

            if (commit is not null)
                sb.Append($"Updated to commit `{commit}`\n\n");
            else if (preserved.Header.Length > 0)
                sb.Append(preserved.Header).Append("\n\n");

            sb.Append(BreakingHeading).Append('\n');
            if (preserved.BreakingPreamble.Length > 0)
                sb.Append('\n').Append(preserved.BreakingPreamble).Append("\n\n");
            sb.Append(ListHeading).Append("\n\n");
            AppendLetterSection(sb, report.Types.Where(t => t.Breaking.Count > 0).Select(t => (t.DisplayName, t.Breaking)));

            sb.Append("\n---\n").Append(ChangesHeading).Append('\n');
            AppendLetterSection(sb, report.Types.Where(t => t.Changes.Count > 0).Select(t => (t.DisplayName, t.Changes)));

            sb.Append("\n---\n").Append(NewTypesHeading).Append('\n');
            AppendLetterSection(sb, report.NewTypes.Select(n => (n, new List<Bullet>())));

            sb.Append("\n---\n").Append(preserved.ObsoleteIntro.Length > 0 ? preserved.ObsoleteIntro : ObsoleteHeading).Append('\n');
            AppendLetterSection(sb, report.Obsoletes.Select(t => (t.DisplayName, t.Breaking)));

            sb.Append("\n---\n").Append(preserved.OtherNotes.Length > 0 ? preserved.OtherNotes : OtherNotesHeading).Append('\n');
            return sb.ToString();
        }

        private static void AppendLetterSection(StringBuilder sb, IEnumerable<(string Name, List<Bullet> Bullets)> entries) {
            var byLetter = entries
                .GroupBy(e => e.Name, StringComparer.Ordinal)
                .Select(g => (Name: g.Key, Bullets: g.SelectMany(e => e.Bullets).ToList()))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToLookup(e => char.ToUpperInvariant(e.Name.TrimStart('_')[0]));

            sb.Append("<details>\n\n");
            var letters = Enumerable.Range('A', 26).Select(c => (char)c).ToList();
            letters.AddRange(byLetter.Select(g => g.Key).Where(c => !letters.Contains(c)).Order());
            foreach (var letter in letters) {
                sb.Append($"- {letter}\n");
                foreach (var (name, bullets) in byLetter[letter]) {
                    sb.Append($"  - {Code(name)}\n");
                    AppendBullets(sb, bullets, 2);
                }
            }
            sb.Append("\n</details>\n");
        }

        private static void AppendBullets(StringBuilder sb, List<Bullet> bullets, int depth) {
            foreach (var bullet in bullets) {
                sb.Append(' ', depth * 2).Append("- ").Append(bullet.Text).Append('\n');
                AppendBullets(sb, bullet.Children, depth + 1);
            }
        }

        /// <summary>Hand written parts of an existing doc that are kept when regenerating it.</summary>
        private sealed record Preserved(string Header, string BreakingPreamble, string ObsoleteIntro, string OtherNotes) {
            public static Preserved From(string? existing) {
                if (existing is null)
                    return new Preserved("", "", "", "");

                var lines = existing.Replace("\r", string.Empty).Split('\n');
                int Find(string prefix) => Array.FindIndex(lines, l => l.StartsWith(prefix, StringComparison.Ordinal));
                string Join(int from, int to) => from < 0 || to <= from ? "" : string.Join('\n', lines[from..to]).Trim('\n', ' ');

                var breaking = Find(BreakingHeading);
                var list = Find(ListHeading);
                // Older docs have no list heading; the notes then end where the list (or its <details>) starts
                if (list < 0 && breaking >= 0)
                    list = Array.FindIndex(lines, breaking + 1, l => l.StartsWith("<details>", StringComparison.Ordinal) || l.StartsWith("- A", StringComparison.Ordinal) || l.StartsWith("## ", StringComparison.Ordinal));
                var obsolete = Find(ObsoleteHeading);
                var obsoleteDetails = obsolete < 0 ? -1 : Array.FindIndex(lines, obsolete, l => l.StartsWith("<details>", StringComparison.Ordinal));
                var otherNotes = Find(OtherNotesHeading);

                return new Preserved(
                    Join(0, breaking < 0 ? 0 : breaking),
                    breaking >= 0 && list > breaking ? Join(breaking + 1, list) : "",
                    obsoleteDetails > obsolete ? Join(obsolete, obsoleteDetails) : "",
                    otherNotes >= 0 ? Join(otherNotes, lines.Length) : "");
            }
        }
    }

    #endregion
}
