using System.Text;
using System.Text.RegularExpressions;

internal enum BreakingRuleBucket {
    SourceBinaryAssemblies,
    SourceBinaryTypes,
    SourceBinaryMembers,
    SourceBinarySignatures,
    SourceBinaryAttributes,
    BehavioralValues,
    BehavioralExceptions,
    BehavioralPlatform,
    BehavioralCode,
    NeedsManualReview
}

internal enum BreakingDisposition {
    Breaking,
    NonBreaking,
    NeedsReview
}

internal sealed record BreakingFinding(
    BreakingDisposition Disposition,
    BreakingRuleBucket RuleBucket,
    string FilePath,
    string Symbol,
    string Reason,
    string Evidence
);

internal static class BreakingChangeAnalyzer {
    private static readonly string[] GeneratedTypeNameFilters = ["Delegates", "Addresses", "MemberFunctionPointers", "VirtualTable", "StaticAddressPointers"];
    private static readonly string[] TrackedAttributePrefixes = ["Obsolete", "Flags", "NonSerialized", "Serializable"];

    private static readonly Regex TypeDeclRegex = new(
        @"^(?<visibility>public|protected)?\s*(?<modifiers>(?:readonly|ref|partial|sealed|abstract)\s+)*(?<kind>class|struct|enum|interface|record)\s+(?<name>[A-Za-z_][A-Za-z0-9_`<>,.]*)\s*(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex MemberDeclRegex = new(
        @"^(?<visibility>public|protected)\s+(?<signature>.+?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<tail>\(|\{|;|=)",
        RegexOptions.Compiled);

    private static readonly Regex MethodNameRegex = new(
        @"(?:public|protected)\s+.*\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<params>[^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex EnumMemberRegex = new(
        @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[^,]+),?\s*$",
        RegexOptions.Compiled);

    public static string AnalyzeDiffToMarkdown(string diffContent, string sourceName) {
        var findings = Analyze(diffContent);
        return RenderMarkdown(findings, sourceName);
    }

    private static List<BreakingFinding> Analyze(string diffContent) {
        var findings = new List<BreakingFinding>();
        var lines = diffContent.Replace("\r", string.Empty).Split('\n');
        var fileDeltas = new Dictionary<string, FileDelta>(StringComparer.Ordinal);

        string currentFile = string.Empty;
        var removed = new List<string>();
        var added = new List<string>();

        void FlushHunk() {
            if (removed.Count == 0 && added.Count == 0)
                return;

            if (string.IsNullOrWhiteSpace(currentFile)) {
                removed.Clear();
                added.Clear();
                return;
            }

            if (!fileDeltas.TryGetValue(currentFile, out var delta)) {
                delta = new FileDelta();
                fileDeltas[currentFile] = delta;
            }

            delta.RemovedLines.AddRange(removed);
            delta.AddedLines.AddRange(added);
            delta.Hunks.Add(new HunkDelta([.. removed], [.. added]));

            removed.Clear();
            added.Clear();
        }

        foreach (var line in lines) {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) {
                FlushHunk();
                currentFile = ParseRightFilePath(line);
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal)) {
                FlushHunk();
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal)) {
                removed.Add(line[1..]);
                continue;
            }

            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal)) {
                added.Add(line[1..]);
            }
        }

        FlushHunk();
        foreach (var (filePath, delta) in fileDeltas) {
            if (ShouldExcludeFile(filePath))
                continue;

            AnalyzeFileDelta(filePath, delta, findings);
        }

        return findings
            .Where(f => !ShouldExcludeFinding(f))
            .DistinctBy(f => (f.Disposition, f.RuleBucket, f.FilePath, f.Symbol, f.Reason, f.Evidence))
            .ToList();
    }

    private static void AnalyzeFileDelta(string filePath, FileDelta delta, List<BreakingFinding> findings) {
        var removedTypes = ParseTypeDeclarations(delta.RemovedLines);
        var addedTypes = ParseTypeDeclarations(delta.AddedLines);

        var removedMembers = ParseMemberDeclarations(delta.RemovedLines);
        var addedMembers = ParseMemberDeclarations(delta.AddedLines);

        var removedAttributes = ParseAttributes(delta.RemovedLines);
        var addedAttributes = ParseAttributes(delta.AddedLines);

        var removedEnumMembers = ParseEnumMembers(delta.RemovedLines);
        var addedEnumMembers = ParseEnumMembers(delta.AddedLines);

        AnalyzeTypeChanges(filePath, removedTypes, addedTypes, findings);
        AnalyzeMemberChanges(filePath, removedMembers, addedMembers, findings);
        AnalyzeAttributeChanges(filePath, removedAttributes, addedAttributes, findings);
        AnalyzeEnumValueChanges(filePath, removedEnumMembers, addedEnumMembers, findings);

        foreach (var hunk in delta.Hunks)
            AnalyzeBehaviorSignals(filePath, hunk.RemovedLines, hunk.AddedLines, findings);
    }

    private static void AnalyzeTypeChanges(string filePath, List<TypeDecl> removed, List<TypeDecl> added, List<BreakingFinding> findings) {
        foreach (var removedType in removed) {
            var match = added.FirstOrDefault(t => t.Name == removedType.Name);
            if (match is null) {
                findings.Add(new BreakingFinding(
                    BreakingDisposition.Breaking,
                    BreakingRuleBucket.SourceBinaryTypes,
                    filePath,
                    removedType.Name,
                    "Type removed or renamed.",
                    removedType.RawLine));
                continue;
            }

            if (!string.Equals(removedType.Kind, match.Kind, StringComparison.Ordinal)) {
                findings.Add(new BreakingFinding(
                    BreakingDisposition.Breaking,
                    BreakingRuleBucket.SourceBinaryTypes,
                    filePath,
                    removedType.Name,
                    $"Type kind changed from {removedType.Kind} to {match.Kind}.",
                    $"- {removedType.RawLine}\n+ {match.RawLine}"));
            }

            if (removedType.Kind == "enum" && !string.Equals(removedType.EnumUnderlyingType, match.EnumUnderlyingType, StringComparison.Ordinal)) {
                findings.Add(new BreakingFinding(
                    BreakingDisposition.Breaking,
                    BreakingRuleBucket.SourceBinaryTypes,
                    filePath,
                    removedType.Name,
                    "Enum underlying type changed.",
                    $"- {removedType.RawLine}\n+ {match.RawLine}"));
            }

            if (removedType.IsReadonly && !match.IsReadonly && removedType.Kind == "struct") {
                findings.Add(new BreakingFinding(
                    BreakingDisposition.Breaking,
                    BreakingRuleBucket.SourceBinaryTypes,
                    filePath,
                    removedType.Name,
                    "Readonly struct changed to mutable struct.",
                    $"- {removedType.RawLine}\n+ {match.RawLine}"));
            }
        }

        foreach (var addedType in added.Where(add => removed.All(rem => rem.Name != add.Name))) {
            findings.Add(new BreakingFinding(
                BreakingDisposition.NonBreaking,
                BreakingRuleBucket.SourceBinaryTypes,
                filePath,
                addedType.Name,
                "New type added.",
                addedType.RawLine));
        }
    }

    private static void AnalyzeMemberChanges(string filePath, List<MemberDecl> removed, List<MemberDecl> added, List<BreakingFinding> findings) {
        var addedPool = new List<MemberDecl>(added);
        foreach (var removedMember in removed) {
            var exactMatch = addedPool.FirstOrDefault(m =>
                string.Equals(NormalizeWhitespace(m.RawLine), NormalizeWhitespace(removedMember.RawLine), StringComparison.Ordinal));
            if (exactMatch is not null) {
                addedPool.Remove(exactMatch);
                continue;
            }

            var match = addedPool.FirstOrDefault(m => m.Name == removedMember.Name && m.IsMethod == removedMember.IsMethod);
            if (match is null) {
                findings.Add(new BreakingFinding(
                    BreakingDisposition.Breaking,
                    BreakingRuleBucket.SourceBinaryMembers,
                    filePath,
                    removedMember.Name,
                    "Public/protected member removed or renamed.",
                    removedMember.RawLine));
                continue;
            }

            if (!string.Equals(NormalizeWhitespace(removedMember.RawLine), NormalizeWhitespace(match.RawLine), StringComparison.Ordinal)) {
                findings.Add(new BreakingFinding(
                    BreakingDisposition.Breaking,
                    BreakingRuleBucket.SourceBinarySignatures,
                    filePath,
                    removedMember.Name,
                    "Member signature changed.",
                    $"- {removedMember.RawLine}\n+ {match.RawLine}"));
            }

            addedPool.Remove(match);
        }

        foreach (var addedMember in addedPool) {
            var isAbstract = addedMember.RawLine.Contains(" abstract ", StringComparison.Ordinal) || addedMember.RawLine.StartsWith("abstract ", StringComparison.Ordinal);
            findings.Add(new BreakingFinding(
                isAbstract ? BreakingDisposition.NeedsReview : BreakingDisposition.NonBreaking,
                isAbstract ? BreakingRuleBucket.SourceBinaryMembers : BreakingRuleBucket.SourceBinaryMembers,
                filePath,
                addedMember.Name,
                isAbstract
                    ? "Abstract member added; breaking status depends on type extensibility per runtime rules."
                    : "New member added.",
                addedMember.RawLine));
        }
    }

    private static void AnalyzeAttributeChanges(string filePath, HashSet<string> removed, HashSet<string> added, List<BreakingFinding> findings) {
        foreach (var removedAttr in removed.Where(attr => !added.Contains(attr))) {
            var disposition = BreakingDisposition.NeedsReview;
            var reason = "Attribute removed; observable-attribute removals are breaking per runtime rules.";

            if (removedAttr.Contains("Obsolete", StringComparison.Ordinal)) {
                disposition = BreakingDisposition.NonBreaking;
                reason = "Obsolete attribute removed; often compatibility-improving, but still needs review for tooling contracts.";
            }

            findings.Add(new BreakingFinding(
                disposition,
                BreakingRuleBucket.SourceBinaryAttributes,
                filePath,
                removedAttr,
                reason,
                $"[{removedAttr}]"));
        }

        foreach (var addedAttr in added.Where(attr => !removed.Contains(attr))) {
            if (addedAttr.StartsWith("Flags", StringComparison.Ordinal)) {
                findings.Add(new BreakingFinding(
                    BreakingDisposition.Breaking,
                    BreakingRuleBucket.SourceBinarySignatures,
                    filePath,
                    "FlagsAttribute",
                    "Adding FlagsAttribute to an enum is disallowed.",
                    $"[{addedAttr}]"));
                continue;
            }

            if (!addedAttr.StartsWith("Obsolete", StringComparison.Ordinal))
                continue;

            var isError = addedAttr.Contains("true", StringComparison.OrdinalIgnoreCase);
            findings.Add(new BreakingFinding(
                isError ? BreakingDisposition.Breaking : BreakingDisposition.NeedsReview,
                BreakingRuleBucket.SourceBinaryAttributes,
                filePath,
                "ObsoleteAttribute",
                isError
                    ? "Obsolete(error: true) added; compile-time breaking for consumers."
                    : "Obsolete added; usually non-breaking but migration-impacting.",
                $"[{addedAttr}]"));
        }
    }

    private static void AnalyzeEnumValueChanges(string filePath, Dictionary<string, string> removed, Dictionary<string, string> added, List<BreakingFinding> findings) {
        foreach (var (name, oldValue) in removed) {
            if (!added.TryGetValue(name, out var newValue))
                continue;

            if (NormalizeWhitespace(oldValue) == NormalizeWhitespace(newValue))
                continue;

            findings.Add(new BreakingFinding(
                BreakingDisposition.Breaking,
                BreakingRuleBucket.BehavioralValues,
                filePath,
                name,
                "Enum member value changed.",
                $"- {name} = {oldValue}\n+ {name} = {newValue}"));
        }
    }

    private static void AnalyzeBehaviorSignals(string filePath, List<string> removed, List<string> added, List<BreakingFinding> findings) {
        if (removed.Any(l => l.Contains("throw ", StringComparison.Ordinal)) || added.Any(l => l.Contains("throw ", StringComparison.Ordinal))) {
            findings.Add(new BreakingFinding(
                BreakingDisposition.NeedsReview,
                BreakingRuleBucket.BehavioralExceptions,
                filePath,
                "ExceptionFlow",
                "Exception throw behavior changed in this hunk; verify against allowed/disallowed exception rules.",
                "throw statements changed"));
        }

        if (removed.Any(l => l.Contains(" event ", StringComparison.Ordinal)) || added.Any(l => l.Contains(" event ", StringComparison.Ordinal))) {
            findings.Add(new BreakingFinding(
                BreakingDisposition.NeedsReview,
                BreakingRuleBucket.BehavioralCode,
                filePath,
                "EventBehavior",
                "Event declarations/usages changed; verify ordering/count semantics.",
                "event-related lines changed"));
        }

        if (removed.Any(l => l.Contains("async", StringComparison.Ordinal)) || added.Any(l => l.Contains("async", StringComparison.Ordinal))) {
            findings.Add(new BreakingFinding(
                BreakingDisposition.NeedsReview,
                BreakingRuleBucket.BehavioralCode,
                filePath,
                "SyncAsyncBehavior",
                "Async usage changed; verify no sync/async contract switch occurred.",
                "async-related lines changed"));
        }
    }

    private static List<TypeDecl> ParseTypeDeclarations(IEnumerable<string> lines) {
        var result = new List<TypeDecl>();
        foreach (var rawLine in lines) {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            var match = TypeDeclRegex.Match(line);
            if (!match.Success)
                continue;

            var visibility = match.Groups["visibility"].Value;
            if (!(visibility == "public" || visibility == "protected"))
                continue;

            var kind = match.Groups["kind"].Value;
            var name = match.Groups["name"].Value;
            var modifiers = match.Groups["modifiers"].Value;
            var rest = match.Groups["rest"].Value;
            string? enumUnderlying = null;
            var colonIndex = rest.IndexOf(':');
            if (kind == "enum" && colonIndex >= 0) {
                var tail = rest[(colonIndex + 1)..].Trim();
                var split = tail.Split([' ', '{'], StringSplitOptions.RemoveEmptyEntries);
                if (split.Length > 0)
                    enumUnderlying = split[0].Trim();
            }

            result.Add(new TypeDecl(
                kind,
                name,
                visibility,
                modifiers.Contains("readonly", StringComparison.Ordinal),
                enumUnderlying,
                line));
        }
        return result;
    }

    private static List<MemberDecl> ParseMemberDeclarations(IEnumerable<string> lines) {
        var result = new List<MemberDecl>();
        foreach (var rawLine in lines) {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("[", StringComparison.Ordinal))
                continue;
            if (line.Contains(" class ", StringComparison.Ordinal) ||
                line.Contains(" struct ", StringComparison.Ordinal) ||
                line.Contains(" enum ", StringComparison.Ordinal) ||
                line.Contains(" interface ", StringComparison.Ordinal) ||
                line.Contains(" record ", StringComparison.Ordinal))
                continue;

            var match = MemberDeclRegex.Match(line);
            if (!match.Success)
                continue;

            var visibility = match.Groups["visibility"].Value;
            var name = match.Groups["name"].Value;
            result.Add(new MemberDecl(name, visibility, line));
        }
        return result;
    }

    private static HashSet<string> ParseAttributes(IEnumerable<string> lines) {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in lines) {
            var line = rawLine.Trim();
            if (!line.StartsWith("[", StringComparison.Ordinal) || !line.EndsWith(']'))
                continue;

            var attribute = line[1..^1].Trim();
            var attributeName = attribute.Split(['(', ' '], StringSplitOptions.RemoveEmptyEntries)[0];
            if (!TrackedAttributePrefixes.Any(prefix => attributeName.StartsWith(prefix, StringComparison.Ordinal)))
                continue;

            set.Add(attribute);
        }
        return set;
    }

    private static Dictionary<string, string> ParseEnumMembers(IEnumerable<string> lines) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in lines) {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal))
                continue;
            if (line.Contains("public ", StringComparison.Ordinal) || line.Contains("private ", StringComparison.Ordinal) || line.Contains("protected ", StringComparison.Ordinal))
                continue;
            if (line.Contains("=>", StringComparison.Ordinal) || line.Contains("(", StringComparison.Ordinal) || !line.Contains('='))
                continue;

            var match = EnumMemberRegex.Match(line);
            if (!match.Success)
                continue;

            result[match.Groups["name"].Value] = match.Groups["value"].Value.Trim();
        }
        return result;
    }

    private static string RenderMarkdown(List<BreakingFinding> findings, string sourceName) {
        var sb = new StringBuilder();
        sb.AppendLine("# Breaking Change Analysis");
        sb.AppendLine();
        sb.AppendLine($"Source Diff: {sourceName}");
        sb.AppendLine($"Generated UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine();
        sb.AppendLine("## Rule Baseline");
        sb.AppendLine("Classifications follow .NET runtime breaking-change rules across Source/Binary (Assemblies, Types, Members, Signatures, Attributes) and Behavioral buckets.");
        sb.AppendLine();

        if (findings.Count == 0) {
            sb.AppendLine("No candidate breaking changes were detected from this diff.");
            return sb.ToString();
        }

        sb.AppendLine("## Summary");
        sb.AppendLine($"- Breaking: {findings.Count(f => f.Disposition == BreakingDisposition.Breaking)}");
        sb.AppendLine($"- NonBreaking: {findings.Count(f => f.Disposition == BreakingDisposition.NonBreaking)}");
        sb.AppendLine($"- NeedsReview: {findings.Count(f => f.Disposition == BreakingDisposition.NeedsReview)}");
        sb.AppendLine();

        foreach (var dispositionGroup in findings
                     .OrderBy(f => f.Disposition)
                     .ThenBy(f => f.RuleBucket)
                     .ThenBy(f => f.FilePath)
                     .ThenBy(f => f.Symbol)
                     .GroupBy(f => f.Disposition)) {
            sb.AppendLine($"## {dispositionGroup.Key}");
            foreach (var ruleGroup in dispositionGroup.GroupBy(f => f.RuleBucket)) {
                sb.AppendLine($"### {ruleGroup.Key}");
                foreach (var finding in ruleGroup) {
                    sb.AppendLine($"- {finding.FilePath} :: {finding.Symbol}");
                    sb.AppendLine($"  - Reason: {finding.Reason}");
                    sb.AppendLine($"  - Evidence: {finding.Evidence.Replace("\n", " | ")}");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string ParseRightFilePath(string diffHeaderLine) {
        var parts = diffHeaderLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return "UnknownFile";

        var right = parts[3];
        return right.StartsWith("b/", StringComparison.Ordinal) ? right[2..] : right;
    }

    private static string NormalizeWhitespace(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    private static bool ShouldExcludeFile(string filePath) => filePath.Contains("FFXIVClientStructs.Interop", StringComparison.Ordinal);

    private static bool ShouldExcludeFinding(BreakingFinding finding) =>
        GeneratedTypeNameFilters.Any(t =>
            string.Equals(finding.Symbol, t, StringComparison.Ordinal) ||
            finding.Symbol.Contains(t, StringComparison.Ordinal));

    private sealed class FileDelta {
        public List<string> RemovedLines { get; } = new();
        public List<string> AddedLines { get; } = new();
        public List<HunkDelta> Hunks { get; } = new();
    }

    private sealed record HunkDelta(List<string> RemovedLines, List<string> AddedLines);

    private sealed record TypeDecl(string Kind, string Name, string Visibility, bool IsReadonly, string? EnumUnderlyingType, string RawLine);

    private sealed record MemberDecl(string Name, string Visibility, string RawLine) {
        public bool IsMethod => MethodNameRegex.IsMatch(RawLine);
    }
}
