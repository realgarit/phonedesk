using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PhoneDesk.LocalizationGuard;

public sealed record LocalizationFinding(string FilePath, int Line, int Column, string Message)
{
    public override string ToString() => $"{FilePath}:{Line}:{Column}: {Message}";
}

public static class LocalizationGuard
{
    private const string PresentationRoot = "src/PhoneDesk.Presentation";
    private const string EnglishCatalog = "src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json";
    private const string GermanCatalog = "src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json";
    private const string UiTextKeySource = "src/PhoneDesk.Presentation/Localization/UiTextKey.cs";

    private static readonly Regex EnumMemberRegex = new(
        @"^\s{4}(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*,?\s*(?://.*)?$",
        RegexOptions.Compiled);

    private static readonly Regex PlaceholderRegex = new(
        @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex FormatPlaceholderRegex = new(
        @"\{\d+(?::[^{}]*)?\}",
        RegexOptions.Compiled);

    private static readonly Regex TranslateKeyRegex = new(
        @"\bKey\s*=\s*(?<key>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex RawUiAssignmentRegex = new(
        "(?<sink>StatusMessage|WaitingMessage|ProgressText|LogMessage|StepResult)\\s*=\\s*(?<value>\"(?:\\\\.|[^\"\\\\])*\")",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RawLogRegex = new(
        "\\b_loggingService\\.Log\\s*\\(\\s*(?<value>\"(?:\\\\.|[^\"\\\\])*\")",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DomainWaitingMessageRegex = new(
        @"\bWaitingMessage\s*=\s*ConstantsService\.Messages\.[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled);

    private const string CSharpStringLiteralPattern =
        """(?:\$?"(?:\\.|[^"\\])*"|@"(?:""|[^"])*")""";

    private static readonly Regex RawCSharpUiAssignmentRegex = new(
        $@"\b(?<sink>Text|Content|Header|Title|Watermark|ToolTip\.Tip|AutomationProperties\.Name|OnContent|OffContent|Message|Description|PrimaryButtonText|SecondaryButtonText|ChromeText)\b\s*=\s*(?<value>{CSharpStringLiteralPattern})",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RawFilePickerTypeRegex = new(
        $@"\bFilePickerFileType\s*\(\s*(?<value>{CSharpStringLiteralPattern})",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RawCSharpUserFieldRegex = new(
        $@"\b(?:private|protected|internal|public)(?:\s+(?:static|readonly|const|virtual|override|new))*\s+string\??\s+(?<field>[A-Za-z_][A-Za-z0-9_]*(?:Message|Title|Description|Label|Status|Subtitle|Welcome|Hint|Prompt|Action|Text))\s*=\s*(?<value>{CSharpStringLiteralPattern})",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RawStringBuilderAppendRegex = new(
        $@"\b(?:doc|report|builder)\.Append(?:Line)?\s*\(\s*(?<value>{CSharpStringLiteralPattern})",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly HashSet<string> VisibleXamlProperties = new(StringComparer.Ordinal)
    {
        "Text",
        "Content",
        "Header",
        "Title",
        "ToolTip.Tip",
        "Watermark",
        "AutomationProperties.Name",
        "OnContent",
        "OffContent"
    };

    private static readonly HashSet<string> ImplicitXamlContentElements = new(StringComparer.Ordinal)
    {
        "Button",
        "CheckBox",
        "ComboBoxItem",
        "ContentControl",
        "HyperlinkButton",
        "Label",
        "ListBoxItem",
        "MenuItem",
        "RadioButton",
        "Run",
        "TabItem",
        "TextBlock",
        "ToggleButton",
        "ToggleSwitch"
    };

    private static readonly HashSet<string> ApprovedRawXamlValues = new(StringComparer.Ordinal)
    {
        "PhoneDesk",
        "UPN",
        "CSV",
        "JSON",
        "UTC",
        "ID",
        "1",
        "2",
        "3",
        "4"
    };

    public static ImmutableArray<LocalizationFinding> Scan(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var root = Path.GetFullPath(repositoryRoot);
        var findings = ImmutableArray.CreateBuilder<LocalizationFinding>();
        var enumKeys = ReadUiTextKeys(root, findings);
        var english = ReadCatalog(root, EnglishCatalog, "English", findings);
        var german = ReadCatalog(root, GermanCatalog, "German", findings);

        ValidateCatalogs(root, enumKeys, english, german, findings);
        ScanXaml(root, enumKeys, findings);
        ScanCSharpSinks(root, findings);

        return findings.ToImmutable();
    }

    private static HashSet<string> ReadUiTextKeys(
        string root,
        ImmutableArray<LocalizationFinding>.Builder findings)
    {
        var path = Path.Combine(root, UiTextKeySource.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            Add(findings, root, path, 1, 1, "UiTextKey.cs was not found.");
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var text = File.ReadAllText(path);
        var enumStart = text.IndexOf("enum UiTextKey", StringComparison.Ordinal);
        if (enumStart < 0)
        {
            Add(findings, root, path, 1, 1, "UiTextKey enum was not found.");
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var openBrace = text.IndexOf('{', enumStart);
        var closeBrace = openBrace < 0 ? -1 : text.IndexOf('}', openBrace + 1);
        if (openBrace < 0 || closeBrace < 0)
        {
            Add(findings, root, path, LineNumber(text, enumStart), ColumnNumber(text, enumStart), "UiTextKey enum has no complete body.");
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var body = text[(openBrace + 1)..closeBrace];
        var bodyOffset = openBrace + 1;
        foreach (var line in Lines(body))
        {
            var match = EnumMemberRegex.Match(line.Text);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["name"].Value;
            var index = bodyOffset + line.Offset + match.Index;
            if (!keys.Add(key))
            {
                Add(findings, root, path, LineNumber(text, index), ColumnNumber(text, index), $"UiTextKey contains duplicate member '{key}'.");
            }
        }

        if (keys.Count == 0)
        {
            Add(findings, root, path, LineNumber(text, enumStart), ColumnNumber(text, enumStart), "UiTextKey enum contains no members.");
        }

        return keys;
    }

    private static CatalogData ReadCatalog(
        string root,
        string relativePath,
        string language,
        ImmutableArray<LocalizationFinding>.Builder findings)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var data = new CatalogData();
        if (!File.Exists(path))
        {
            Add(findings, root, path, 1, 1, $"{language} catalog was not found.");
            return data;
        }

        var text = File.ReadAllText(path);
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                AllowMultipleValues = true
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                Add(findings, root, path, 1, 1, $"{language} catalog must contain a JSON object.");
                return data;
            }

            ReadObject(ref reader, isRoot: true, data, root, path, text, bytes, findings);
            if (reader.Read())
            {
                var trailingLocation = Location(text, bytes, reader.TokenStartIndex);
                Add(
                    findings,
                    root,
                    path,
                    trailingLocation.Line,
                    trailingLocation.Column,
                    $"{language} catalog contains trailing JSON content.");
            }
        }
        catch (JsonException ex)
        {
            var line = checked((int)ex.LineNumber.GetValueOrDefault()) + 1;
            var column = checked((int)ex.BytePositionInLine.GetValueOrDefault()) + 1;
            Add(findings, root, path, line, column, $"{language} catalog contains invalid JSON: {ex.Message}");
        }

        return data;
    }

    private static void ReadObject(
        ref Utf8JsonReader reader,
        bool isRoot,
        CatalogData data,
        string root,
        string path,
        string text,
        byte[] bytes,
        ImmutableArray<LocalizationFinding>.Builder findings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                Add(findings, root, path, Location(text, bytes, reader.TokenStartIndex).Line, Location(text, bytes, reader.TokenStartIndex).Column, "JSON object contains an invalid property.");
                reader.Skip();
                continue;
            }

            var name = reader.GetString() ?? string.Empty;
            var propertyLocation = Location(text, bytes, reader.TokenStartIndex);
            if (!names.Add(name))
            {
                Add(findings, root, path, propertyLocation.Line, propertyLocation.Column, $"duplicate JSON key '{name}'.");
            }

            if (!reader.Read())
            {
                return;
            }

            if (isRoot)
            {
                if (reader.TokenType != JsonTokenType.String)
                {
                    Add(findings, root, path, propertyLocation.Line, propertyLocation.Column, $"Catalog key '{name}' must contain a string value.");
                }
                else
                {
                    data.Values[name] = reader.GetString() ?? string.Empty;
                    data.Keys.Add(name);
                }
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                ReadObject(ref reader, isRoot: false, data, root, path, text, bytes, findings);
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                ReadArray(ref reader, data, root, path, text, bytes, findings);
            }
        }

        Add(findings, root, path, 1, 1, "JSON object is not closed.");
    }

    private static void ReadArray(
        ref Utf8JsonReader reader,
        CatalogData data,
        string root,
        string path,
        string text,
        byte[] bytes,
        ImmutableArray<LocalizationFinding>.Builder findings)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                ReadObject(ref reader, isRoot: false, data, root, path, text, bytes, findings);
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                ReadArray(ref reader, data, root, path, text, bytes, findings);
            }
        }

        Add(findings, root, path, 1, 1, "JSON array is not closed.");
    }

    private static void ValidateCatalogs(
        string root,
        HashSet<string> enumKeys,
        CatalogData english,
        CatalogData german,
        ImmutableArray<LocalizationFinding>.Builder findings)
    {
        var englishPath = Path.Combine(root, EnglishCatalog.Replace('/', Path.DirectorySeparatorChar));
        var germanPath = Path.Combine(root, GermanCatalog.Replace('/', Path.DirectorySeparatorChar));

        foreach (var key in enumKeys.Order(StringComparer.Ordinal))
        {
            if (!english.Keys.Contains(key))
            {
                Add(findings, root, englishPath, 1, 1, $"English catalog is missing key '{key}'.");
            }

            if (!german.Keys.Contains(key))
            {
                Add(findings, root, germanPath, 1, 1, $"German catalog is missing key '{key}'.");
            }
        }

        foreach (var key in english.Keys.Order(StringComparer.Ordinal))
        {
            if (!enumKeys.Contains(key))
            {
                Add(findings, root, englishPath, 1, 1, $"English catalog contains unknown key '{key}'.");
            }
        }

        foreach (var key in german.Keys.Order(StringComparer.Ordinal))
        {
            if (!enumKeys.Contains(key))
            {
                Add(findings, root, germanPath, 1, 1, $"German catalog contains unknown key '{key}'.");
            }
        }

        foreach (var key in enumKeys.Order(StringComparer.Ordinal))
        {
            if (!english.Values.TryGetValue(key, out var englishValue)
                || !german.Values.TryGetValue(key, out var germanValue))
            {
                continue;
            }

            var englishPlaceholders = Placeholders(englishValue);
            var germanPlaceholders = Placeholders(germanValue);
            if (!PlaceholderSetsEqual(englishPlaceholders, germanPlaceholders))
            {
                Add(
                    findings,
                    root,
                    germanPath,
                    1,
                    1,
                    $"German catalog placeholder mismatch for key '{key}': English has {FormatPlaceholders(englishPlaceholders)}, German has {FormatPlaceholders(germanPlaceholders)}.");
            }
        }
    }

    private static void ScanXaml(
        string root,
        HashSet<string> enumKeys,
        ImmutableArray<LocalizationFinding>.Builder findings)
    {
        var directory = Path.Combine(root, PresentationRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.axaml", SearchOption.AllDirectories))
        {
            if (IsArchived(path))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            XDocument document;
            try
            {
                document = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            }
            catch (XmlException ex)
            {
                Add(findings, root, path, ex.LineNumber, ex.LinePosition, $"XAML contains invalid XML: {ex.Message}");
                continue;
            }

            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    if (!VisibleXamlProperties.Contains(attribute.Name.LocalName))
                    {
                        continue;
                    }

                    var value = attribute.Value;
                    var lineInfo = (IXmlLineInfo)attribute;
                    var line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;
                    var column = lineInfo.HasLineInfo() ? lineInfo.LinePosition : 1;
                    CheckXamlValue(value, attribute.Name.LocalName, enumKeys, root, path, line, column, findings);
                }

                var propertyElementName = element.Name.LocalName;
                var propertyName = propertyElementName[(propertyElementName.LastIndexOf('.') + 1)..];
                if (propertyElementName.Contains('.', StringComparison.Ordinal))
                {
                    if (!VisibleXamlProperties.Contains(propertyElementName)
                        && !VisibleXamlProperties.Contains(propertyName))
                    {
                        continue;
                    }

                    foreach (var textNode in element.DescendantNodes().OfType<XText>())
                    {
                        CheckXamlTextNode(textNode, propertyName, enumKeys, root, path, findings);
                    }
                }
                else if (ImplicitXamlContentElements.Contains(element.Name.LocalName))
                {
                    foreach (var textNode in element.Nodes().OfType<XText>())
                    {
                        CheckXamlTextNode(textNode, element.Name.LocalName, enumKeys, root, path, findings);
                    }
                }
            }
        }
    }

    private static void CheckXamlTextNode(
        XText textNode,
        string propertyName,
        HashSet<string> enumKeys,
        string root,
        string path,
        ImmutableArray<LocalizationFinding>.Builder findings)
    {
        var value = textNode.Value.Trim();
        if (value.Length == 0)
        {
            return;
        }

        var lineInfo = (IXmlLineInfo)textNode;
        var line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;
        var column = lineInfo.HasLineInfo() ? lineInfo.LinePosition : 1;
        CheckXamlValue(value, propertyName, enumKeys, root, path, line, column, findings);
    }

    private static void CheckXamlValue(
        string value,
        string propertyName,
        HashSet<string> enumKeys,
        string root,
        string path,
        int line,
        int column,
        ImmutableArray<LocalizationFinding>.Builder findings)
    {
        if (value.Length == 0 || ApprovedRawXamlValues.Contains(value))
        {
            return;
        }

        if (!value.StartsWith('{'))
        {
            Add(
                findings,
                root,
                path,
                line,
                column,
                $"Raw visible XAML {propertyName} value '{value}' must use a typed translation.");
            return;
        }

        if (!IsBalancedMarkupExpression(value))
        {
            Add(
                findings,
                root,
                path,
                line,
                column,
                $"Visible XAML {propertyName} markup '{value}' is not balanced.");
            return;
        }

        if (value.StartsWith("{loc:Translate", StringComparison.Ordinal))
        {
            var keyMatch = TranslateKeyRegex.Match(value);
            if (!keyMatch.Success)
            {
                Add(findings, root, path, line, column, "Translate markup must specify a UiTextKey.");
            }
            else if (!enumKeys.Contains(keyMatch.Groups["key"].Value))
            {
                Add(findings, root, path, line, column, $"Translate markup references unknown UiTextKey '{keyMatch.Groups["key"].Value}'.");
            }

            return;
        }

        if (value.StartsWith("{Binding", StringComparison.Ordinal))
        {
            if (!IsSafeBinding(value))
            {
                Add(
                    findings,
                    root,
                    path,
                    line,
                    column,
                    $"Visible XAML {propertyName} binding '{value}' contains a raw fallback or format string.");
            }

            return;
        }

        if (value.StartsWith("{DynamicResource", StringComparison.Ordinal)
            || value.StartsWith("{x:Static", StringComparison.Ordinal))
        {
            return;
        }

        Add(
            findings,
            root,
            path,
            line,
            column,
            $"Visible XAML {propertyName} markup '{value}' must use a typed translation.");
    }

    private static bool IsSafeBinding(string value)
    {
        var innerMarkup = value[1..^1].Trim();
        var bindingBody = innerMarkup["Binding".Length..];
        foreach (var segment in SplitTopLevelSegments(bindingBody))
        {
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var name = segment[..equalsIndex].Trim();
            var parameter = segment[(equalsIndex + 1)..].Trim();
            if (name.Equals("StringFormat", StringComparison.Ordinal))
            {
                if (parameter.StartsWith('{'))
                {
                    if (!IsBalancedMarkupExpression(parameter))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!TryUnquote(parameter, out var format))
                    {
                        return false;
                    }

                    if (format.StartsWith("{}", StringComparison.Ordinal))
                    {
                        format = format[2..];
                    }

                    if (FormatPlaceholderRegex.Replace(format, string.Empty).Length != 0)
                    {
                        return false;
                    }
                }
            }
            else if (name is "FallbackValue" or "TargetNullValue" && !IsNestedMarkup(parameter))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> SplitTopLevelSegments(string text)
    {
        var segments = new List<string>();
        var segmentStart = 0;
        var nestedDepth = 0;
        var quote = '\0';

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '{')
            {
                nestedDepth++;
            }
            else if (character == '}')
            {
                nestedDepth--;
            }
            else if (character == ',' && nestedDepth == 0)
            {
                segments.Add(text[segmentStart..index].Trim());
                segmentStart = index + 1;
            }
        }

        if (segmentStart < text.Length)
        {
            segments.Add(text[segmentStart..].Trim());
        }

        return segments;
    }

    private static void ScanCSharpSinks(
        string root,
        ImmutableArray<LocalizationFinding>.Builder findings)
    {
        var directory = Path.Combine(root, PresentationRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGenerated(path) || IsArchived(path))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (Match match in RawUiAssignmentRegex.Matches(text))
            {
                var value = match.Groups["value"].Value;
                if (value is "\"\"" or "string.Empty")
                {
                    continue;
                }

                Add(
                    findings,
                    root,
                    path,
                    LineNumber(text, match.Index),
                    ColumnNumber(text, match.Index),
                    $"Raw UI sink {match.Groups["sink"].Value} must use GetText with a UiTextKey.");
            }

            foreach (Match match in RawCSharpUiAssignmentRegex.Matches(text))
            {
                var value = match.Groups["value"].Value;
                if (IsEmptyCSharpString(value))
                {
                    continue;
                }

                Add(
                    findings,
                    root,
                    path,
                    LineNumber(text, match.Index),
                    ColumnNumber(text, match.Index),
                    $"Raw C# UI sink {match.Groups["sink"].Value} must use GetText with a UiTextKey.");
            }

            foreach (Match match in RawFilePickerTypeRegex.Matches(text))
            {
                Add(
                    findings,
                    root,
                    path,
                    LineNumber(text, match.Index),
                    ColumnNumber(text, match.Index),
                    "File picker type labels must use GetText with a UiTextKey.");
            }

            foreach (Match match in RawCSharpUserFieldRegex.Matches(text))
            {
                var value = match.Groups["value"].Value;
                if (IsEmptyCSharpString(value))
                {
                    continue;
                }

                Add(
                    findings,
                    root,
                    path,
                    LineNumber(text, match.Index),
                    ColumnNumber(text, match.Index),
                    $"Raw user-visible C# field {match.Groups["field"].Value} must use a UiTextKey.");
            }

            foreach (Match match in RawStringBuilderAppendRegex.Matches(text))
            {
                var value = match.Groups["value"].Value;
                if (!HasVisibleLiteralText(value))
                {
                    continue;
                }

                Add(
                    findings,
                    root,
                    path,
                    LineNumber(text, match.Index),
                    ColumnNumber(text, match.Index),
                    "Raw report text must use GetText with a UiTextKey.");
            }

            foreach (Match match in RawLogRegex.Matches(text))
            {
                Add(
                    findings,
                    root,
                    path,
                    LineNumber(text, match.Index),
                    ColumnNumber(text, match.Index),
                    "Displayed log text must use LogLocalized with a UiTextKey.");
            }

            foreach (Match match in DomainWaitingMessageRegex.Matches(text))
            {
                Add(
                    findings,
                    root,
                    path,
                    LineNumber(text, match.Index),
                    ColumnNumber(text, match.Index),
                    "WaitingMessage must use a localized UiTextKey instead of a Domain message constant.");
            }
        }
    }

    private static bool PlaceholderSetsEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var count) || count != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEmptyCSharpString(string value)
        => value is "\"\"" or "string.Empty" or "@\"\"";

    private static bool HasVisibleLiteralText(string value)
    {
        var withoutInterpolations = Regex.Replace(value, @"\{[^{}]*\}", string.Empty);
        var withoutEscapedBraces = withoutInterpolations.Replace("{{", string.Empty, StringComparison.Ordinal)
            .Replace("}}", string.Empty, StringComparison.Ordinal);
        var literal = withoutEscapedBraces.Trim('"', '@', '$', ' ', '\t', '\r', '\n');
        return literal.Any(char.IsLetter);
    }

    private static bool IsGenerated(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, int> Placeholders(string value)
    {
        var placeholders = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in PlaceholderRegex.Matches(value))
        {
            var name = match.Groups["name"].Value;
            placeholders[name] = placeholders.TryGetValue(name, out var count) ? count + 1 : 1;
        }

        return placeholders;
    }

    private static string FormatPlaceholders(IReadOnlyDictionary<string, int> placeholders)
        => placeholders.Count == 0
            ? "none"
            : string.Join(", ", placeholders.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} x{pair.Value}"));

    private static bool IsNestedMarkup(string value)
        => value.StartsWith('{') && IsBalancedMarkupExpression(value);

    private static bool IsBalancedMarkupExpression(string value)
    {
        if (value.Length < 3 || value[0] != '{' || value[^1] != '}')
        {
            return false;
        }

        var depth = 0;
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth < 0)
            {
                return false;
            }
        }

        return quote == '\0' && depth == 0;
    }

    private static bool TryUnquote(string value, out string unquoted)
    {
        if (value.Length >= 2
            && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
        {
            unquoted = value[1..^1];
            return true;
        }

        unquoted = string.Empty;
        return false;
    }

    private static bool IsArchived(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("Archive", StringComparison.OrdinalIgnoreCase));

    private static void Add(
        ImmutableArray<LocalizationFinding>.Builder findings,
        string root,
        string path,
        int line,
        int column,
        string message)
        => findings.Add(new LocalizationFinding(DisplayPath(root, path), line, column, message));

    private static string DisplayPath(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static int LineNumber(string text, int index)
        => 1 + text[..Math.Clamp(index, 0, text.Length)].Count(character => character == '\n');

    private static int ColumnNumber(string text, int index)
    {
        var safeIndex = Math.Clamp(index, 0, text.Length);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, safeIndex - 1));
        return safeIndex - lineStart;
    }

    private static SourceLocation Location(string text, byte[] bytes, long byteOffset)
    {
        var safeOffset = Math.Clamp(byteOffset, 0, bytes.Length);
        var prefix = Encoding.UTF8.GetString(bytes, 0, checked((int)safeOffset));
        return new SourceLocation(LineNumber(prefix, prefix.Length), ColumnNumber(prefix, prefix.Length));
    }

    private static IEnumerable<SourceLine> Lines(string text)
    {
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index != text.Length && text[index] != '\n')
            {
                continue;
            }

            var length = index - start;
            if (length > 0 && text[start + length - 1] == '\r')
            {
                length--;
            }

            yield return new SourceLine(text.Substring(start, length), start);
            start = index + 1;
        }
    }

    private sealed class CatalogData
    {
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct SourceLine(string Text, int Offset);

    private readonly record struct SourceLocation(int Line, int Column);
}
