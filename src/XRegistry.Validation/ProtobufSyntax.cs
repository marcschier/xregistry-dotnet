using System.Globalization;
using System.Numerics;
using System.Text;

namespace XRegistry.Validation;

internal sealed class ProtobufSyntax(ValidationContext context)
{
    private ValidationContext Context => context;
    private static readonly HashSet<string> Scalars = new(StringComparer.Ordinal)
    {
        "double", "float", "int32", "int64", "uint32", "uint64", "sint32", "sint64",
        "fixed32", "fixed64", "sfixed32", "sfixed64", "bool", "string", "bytes"
    };
    private readonly Dictionary<string, Definition> _definitions = new(StringComparer.Ordinal);
    private readonly List<(string Type, string Scope, bool MessageOnly)> _references = [];
    private readonly List<Field> _fields = [];
    private readonly HashSet<string> _documents = new(StringComparer.Ordinal);
    internal IEnumerable<(string Name, bool Selectable)> RootDeclarations
        => _definitions.Where(p => p.Value.IsRoot).Select(p => (p.Key, !p.Value.IsEnum));

    internal async ValueTask ValidateAsync(ReadOnlyMemory<byte> document, int syntax, string? rootReference = null)
    {
        var resolveUris = rootReference is not null && context.Options.DocumentUri is not null;
        var rootName = resolveUris ? context.Options.DocumentUri!.AbsoluteUri : rootReference ?? "root.proto";
        var pending = new Queue<(string Name, ReadOnlyMemory<byte> Bytes, int? Syntax)>();
        _documents.Add(rootName);
        pending.Enqueue((rootName, document, syntax));
        while (pending.TryDequeue(out var item))
        {
            var parser = new Parser(this, Lex(item.Bytes), item.Syntax, item.Name == rootName);
            var imports = parser.Parse();
            foreach (var import in imports)
            {
                context.Work();
                var key = resolveUris ? new Uri(new Uri(item.Name, UriKind.Absolute), import).AbsoluteUri : import;
                if (_documents.Add(key))
                {
                    pending.Enqueue((key, await context.ResolveAsync(key, "$/import").ConfigureAwait(false), null));
                }
            }
        }

        foreach (var reference in _references)
        {
            var resolved = Resolve(reference.Type, reference.Scope);
            Require(resolved is not null && (!reference.MessageOnly || !resolved.IsEnum),
                "protobuf.type", "An undeclared or wrong-kind Protobuf type is referenced.");
        }

        foreach (var field in _fields)
        {
            var resolved = Scalars.Contains(field.Type) ? null : Resolve(field.Type, field.Scope);
            if (field.Packed)
            {
                Require(field.Label == "repeated" && field.Type is not ("string" or "bytes" or "map") &&
                    (Scalars.Contains(field.Type) || resolved is { IsEnum: true }),
                    "protobuf.packed", "Only repeated packable scalars or enums may be packed.");
            }

            if (field.Default is { } value)
            {
                ValidateDefault(field, value, resolved);
            }
        }
    }

    private Definition? Resolve(string type, string scope)
    {
        context.Work();
        if (type.StartsWith('.'))
        {
            return _definitions.GetValueOrDefault(type[1..]);
        }

        for (var prefix = scope; ; prefix = prefix.Contains('.', StringComparison.Ordinal) ? prefix[..prefix.LastIndexOf('.')] : "")
        {
            if (_definitions.TryGetValue(prefix.Length == 0 ? type : prefix + "." + type, out var result))
            {
                return result;
            }

            if (prefix.Length == 0)
            {
                return null;
            }
        }
    }

    private static void ValidateDefault(Field field, Token value, Definition? resolved)
    {
        Require(field.Label is not ("repeated" or "oneof") && field.Type != "map",
            "protobuf.default", "Repeated, map and oneof fields have no explicit default.");
        if (field.Type is "string" or "bytes")
        {
            Require(value.Kind == TokenKind.String, "protobuf.default", "A string or bytes default requires a string literal.");
        }
        else if (field.Type == "bool")
        {
            Require(value.Text is "true" or "false", "protobuf.default", "A boolean default must be true or false.");
        }
        else if (field.Type is "float" or "double")
        {
            Require(value.Text is "inf" or "-inf" or "nan" ||
                double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                "protobuf.default", "A floating-point default is malformed.");
        }
        else if (Scalars.Contains(field.Type))
        {
            var number = Integer(value);
            var bits = field.Type.EndsWith("32", StringComparison.Ordinal) ? 32 : 64;
            var unsigned = field.Type.StartsWith('u') || field.Type.StartsWith("fixed", StringComparison.Ordinal);
            var minimum = unsigned ? BigInteger.Zero : -(BigInteger.One << (bits - 1));
            var maximum = (BigInteger.One << (unsigned ? bits : bits - 1)) - 1;
            Require(number >= minimum && number <= maximum, "protobuf.default", "The integer default is outside its field range.");
        }
        else
        {
            Require(resolved is { IsEnum: true } && resolved.EnumNames.Contains(value.Text),
                "protobuf.default", "Only a declared enum member can be a user-type default.");
        }
    }

    private List<Token> Lex(ReadOnlyMemory<byte> document)
    {
        context.Bytes(document.Length, "$");
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(document.Span);
        }
        catch (DecoderFallbackException)
        {
            throw new ValidationFailure(DocumentValidationStatus.Invalid, new("$", "protobuf.utf8", "The schema is not valid UTF-8."));
        }

        var tokens = new List<Token>();
        var depth = 0;
        var index = 0;
        while (index < text.Length)
        {
            context.Work();
            var value = text[index];
            if (char.IsWhiteSpace(value))
            {
                index++;
                continue;
            }

            if (value == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                index = text.IndexOf('\n', index + 2);
                if (index < 0)
                {
                    break;
                }

                continue;
            }

            if (value == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                Require(end >= 0, "protobuf.comment", "The block comment is unterminated.");
                index = end + 2;
                continue;
            }

            var start = index++;
            Token token;
            if (value is '"' or '\'')
            {
                var content = new StringBuilder();
                var closed = false;
                while (index < text.Length)
                {
                    var character = text[index++];
                    if (character == value)
                    {
                        closed = true;
                        break;
                    }

                    Require(character is not ('\r' or '\n' or '\0'), "protobuf.string", "A string literal contains a raw line break or NUL.");
                    if (character == '\\')
                    {
                        Require(index < text.Length, "protobuf.string", "A string escape is truncated.");
                        character = text[index++];
                        Require(character is 'a' or 'b' or 'f' or 'n' or 'r' or 't' or 'v' or '\\' or '\'' or '"' ||
                            character is >= '0' and <= '7' || character is 'x' or 'X' or 'u' or 'U',
                            "protobuf.string", "The string escape is not supported by Protobuf syntax.");
                        if (character is 'x' or 'X' or 'u' or 'U')
                        {
                            var digits = character is 'u' ? 4 : character is 'U' ? 8 : 2;
                            Require(index + digits <= text.Length && text.AsSpan(index, digits).ToArray().All(char.IsAsciiHexDigit),
                                "protobuf.string", "The string hexadecimal escape is malformed.");
                            content.Append('\\').Append(character).Append(text.AsSpan(index, digits));
                            index += digits;
                            continue;
                        }
                    }

                    content.Append(character);
                }

                Require(closed, "protobuf.string", "The string literal is unterminated.");
                token = new(TokenKind.String, content.ToString());
            }
            else if (char.IsAsciiLetter(value) || value == '_')
            {
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_'))
                {
                    index++;
                }

                token = new(TokenKind.Identifier, text[start..index]);
            }
            else if (char.IsAsciiDigit(value))
            {
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '.' ||
                    text[index] is '+' or '-' && text[index - 1] is 'e' or 'E'))
                {
                    index++;
                }

                token = new(TokenKind.Number, text[start..index]);
            }
            else
            {
                Require("{}[]().,;=<>:+-".Contains(value, StringComparison.Ordinal),
                    "protobuf.token", "An invalid character occurs outside a string.");
                if (value == '{')
                {
                    depth++;
                }
                else if (value == '}')
                {
                    Require(depth > 0, "protobuf.brace", "An unmatched closing brace was found.");
                    depth--;
                }

                token = new(TokenKind.Symbol, value.ToString());
            }

            context.Node(depth + 1);
            tokens.Add(token);
        }

        Require(depth == 0, "protobuf.brace", "A declaration is missing a closing brace.");
        tokens.Add(new(TokenKind.End, ""));
        return tokens;
    }

    private static BigInteger Integer(Token token)
    {
        Require(token.Kind == TokenKind.Number, "protobuf.integer", "An integer literal is required.");
        var text = token.Text;
        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
        {
            text = text[1..];
        }

        var radix = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 :
            text.Length > 1 && text[0] == '0' ? 8 : 10;
        if (radix == 16)
        {
            text = text[2..];
        }

        Require(text.Length > 0, "protobuf.integer", "The integer literal has no digits.");
        BigInteger result = 0;
        foreach (var character in text)
        {
            var digit = char.IsAsciiDigit(character) ? character - '0' :
                character is >= 'a' and <= 'f' ? character - 'a' + 10 :
                character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
            Require(digit >= 0 && digit < radix, "protobuf.integer", "The integer literal has invalid digits.");
            result = result * radix + digit;
        }

        return negative ? -result : result;
    }

    private static void Require(bool condition, string code, string detail) => ValidationContext.Require(condition, "$", code, detail);
    private static void Unsupported(string detail) => ValidationContext.Fail(DocumentValidationStatus.Unsupported, "$", "protobuf.unsupported", detail);

    private enum TokenKind { Identifier, Number, String, Symbol, End }
    private sealed record Token(TokenKind Kind, string Text);
    private sealed record Field(string Name, int Number, string Type, string Scope, string Label, bool Packed, Token? Default);
    private sealed class Definition(bool isEnum, bool isRoot)
    {
        internal bool IsEnum { get; } = isEnum;
        internal bool IsRoot { get; } = isRoot;
        internal HashSet<string> EnumNames { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Parser(ProtobufSyntax owner, List<Token> tokens, int? expectedSyntax, bool isRoot)
    {
        private int _index;
        private int _syntax = 2;
        private string _package = "";
        private readonly List<string> _imports = [];
        private Token Current => tokens[_index];

        internal List<string> Parse()
        {
            if (Take("syntax"))
            {
                Expect("=");
                var syntax = Literal();
                Require(syntax.Text is "proto2" or "proto3", "protobuf.syntax", "Unsupported syntax discriminator.");
                _syntax = syntax.Text == "proto3" ? 3 : 2;
                Expect(";");
            }

            Require(expectedSyntax is null || expectedSyntax == _syntax,
                "protobuf.syntax", "The document syntax disagrees with the declared format.");
            var packageSeen = false;
            while (Current.Kind != TokenKind.End)
            {
                if (Take(";")) { continue; }
                if (Take("package"))
                {
                    Require(!packageSeen, "protobuf.package", "Only one package declaration is allowed.");
                    _package = TypeName();
                    packageSeen = true;
                    Expect(";");
                }
                else if (Take("import"))
                {
                    if (!Take("public")) { Take("weak"); }
                    var reference = Literal().Text;
                    Require(reference.Length > 0 && !reference.Contains('\\', StringComparison.Ordinal) &&
                        !reference.StartsWith('/') && !reference.Split('/').Any(static part => part is "" or "." or ".."),
                        "protobuf.import", "The import name must be an explicit relative schema path.");
                    _imports.Add(reference);
                    Expect(";");
                }
                else if (Take("message")) { Message(_package); }
                else if (Take("enum")) { Enumeration(_package); }
                else if (Take("service")) { Service(); }
                else if (Take("option")) { Option(); }
                else if (Take("extend")) { Unsupported("Custom extensions are not yet supported by this validator."); }
                else { Fail("Unexpected top-level declaration."); }
            }

            return _imports;
        }

        private void Message(string scope)
        {
            var name = Identifier();
            var full = scope.Length == 0 ? name : scope + "." + name;
            Require(owner._definitions.TryAdd(full, new(false, isRoot)), "protobuf.duplicate", "The type name is duplicated.");
            Expect("{");
            var fields = new List<Field>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var numbers = new HashSet<int>();
            var reservedNames = new HashSet<string>(StringComparer.Ordinal);
            var reservedNumbers = new List<(int First, int Last)>();
            while (!Take("}"))
            {
                if (Take(";")) { continue; }
                if (Take("message")) { Message(full); }
                else if (Take("enum")) { Enumeration(full); }
                else if (Take("option")) { Option(); }
                else if (Take("extensions") || Take("extend") || Take("group"))
                {
                    Unsupported("Protobuf extensions and groups require a separate declared validator policy.");
                }
                else if (Take("reserved")) { Reserved(reservedNames, reservedNumbers); }
                else if (Take("oneof"))
                {
                    var oneof = Identifier();
                    Require(names.Add(oneof), "protobuf.duplicate", "Oneof and field names must be unique.");
                    Expect("{");
                    while (!Take("}"))
                    {
                        if (Take("option")) { Option(); }
                        else if (!Take(";")) { AddField(Field(full, true)); }
                    }
                }
                else { AddField(Field(full, false)); }
            }

            foreach (var field in fields)
            {
                Require(!reservedNames.Contains(field.Name) &&
                    !reservedNumbers.Any(range => field.Number >= range.First && field.Number <= range.Last),
                    "protobuf.reserved", "A field reuses a reserved name or number.");
            }

            void AddField(Field field)
            {
                Require(names.Add(field.Name) && numbers.Add(field.Number),
                    "protobuf.duplicate", "Message field names and numbers must be unique.");
                fields.Add(field);
                owner._fields.Add(field);
            }
        }

        private Field Field(string scope, bool oneof)
        {
            var label = oneof ? "oneof" : "";
            if (Current.Text is "optional" or "required" or "repeated")
            {
                Require(!oneof, "protobuf.oneof", "Oneof fields cannot have labels.");
                label = Current.Text;
                _index++;
            }

            Require(_syntax != 3 || label != "required", "protobuf.label", "Proto3 forbids required fields.");
            var map = Take("map");
            string type;
            if (map)
            {
                Require(label.Length == 0, "protobuf.map", "Map fields cannot have labels or belong to a oneof.");
                Expect("<");
                var key = Identifier();
                Require(Scalars.Contains(key) && key is not ("bytes" or "double" or "float"),
                    "protobuf.map", "The map key must be integral, boolean, or string.");
                Expect(",");
                Reference(TypeName(), scope);
                Expect(">");
                type = "map";
            }
            else
            {
                Require(_syntax != 2 || label.Length != 0, "protobuf.label", "Proto2 fields require a label.");
                type = TypeName();
                Reference(type, scope);
            }

            var name = Identifier();
            Expect("=");
            var number = Tag();
            Token? defaultValue = null;
            var packed = false;
            if (Take("["))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    if (Current.Text == "(") { Unsupported("Custom field options require an explicit extension policy."); }
                    var option = Identifier();
                    Require(seen.Add(option), "protobuf.option", "Field options must be unique.");
                    Expect("=");
                    var value = Constant();
                    switch (option)
                    {
                        case "default":
                            Require(_syntax == 2, "protobuf.default", "Proto3 fields cannot have explicit defaults.");
                            defaultValue = value;
                            break;
                        case "packed":
                            Require(value.Text is "true" or "false", "protobuf.option", "packed must be boolean.");
                            packed = value.Text == "true";
                            break;
                        case "json_name":
                            Require(value.Kind == TokenKind.String, "protobuf.option", "json_name must be a string.");
                            break;
                        case "deprecated":
                        case "lazy":
                        case "weak":
                            Require(value.Text is "true" or "false", "protobuf.option", "This field option must be boolean.");
                            break;
                        default:
                            Unsupported("This field option has no implemented validation policy.");
                            break;
                    }
                }
                while (Take(","));
                Expect("]");
            }

            Expect(";");
            return new(name, number, type, scope, label, packed, defaultValue);
        }

        private void Enumeration(string scope)
        {
            var name = Identifier();
            var definition = new Definition(true, isRoot);
            Require(owner._definitions.TryAdd(scope.Length == 0 ? name : scope + "." + name, definition),
                "protobuf.duplicate", "An enum type name is duplicated.");
            Expect("{");
            var values = new List<(string Name, int Number)>();
            var allowAlias = false;
            var reservedNames = new HashSet<string>(StringComparer.Ordinal);
            var reservedNumbers = new List<(int First, int Last)>();
            while (!Take("}"))
            {
                if (Take(";")) { continue; }
                if (Take("option"))
                {
                    var option = Identifier();
                    Expect("=");
                    var value = Constant();
                    Expect(";");
                    Require(value.Text is "true" or "false", "protobuf.option", "The enum option must be boolean.");
                    if (option == "allow_alias") { allowAlias = value.Text == "true"; }
                    else if (option != "deprecated") { Unsupported("Unsupported enum option."); }
                }
                else if (Take("reserved")) { Reserved(reservedNames, reservedNumbers, enumeration: true); }
                else
                {
                    var member = Identifier();
                    Expect("=");
                    var number = Integer(Constant());
                    Require(number >= int.MinValue && number <= int.MaxValue, "protobuf.enum", "Enum values must be Int32.");
                    Require(definition.EnumNames.Add(member), "protobuf.duplicate", "The enum value name is duplicated.");
                    if (Take("["))
                    {
                        Require(Identifier() == "deprecated", "protobuf.option", "Unsupported enum value option.");
                        Expect("=");
                        Require(Constant().Text is "true" or "false", "protobuf.option", "deprecated must be boolean.");
                        Expect("]");
                    }

                    Expect(";");
                    values.Add((member, (int)number));
                }
            }

            Require(values.Count > 0 && (_syntax != 3 || values[0].Number == 0),
                "protobuf.enum", "An enum needs a first value, which must be zero in proto3.");
            Require(allowAlias || values.Select(static value => value.Number).Distinct().Count() == values.Count,
                "protobuf.enum", "Duplicate enum numbers require allow_alias.");
            Require(values.All(value => !reservedNames.Contains(value.Name) &&
                !reservedNumbers.Any(range => value.Number >= range.First && value.Number <= range.Last)),
                "protobuf.reserved", "An enum value uses a reserved name or number.");
        }

        private void Reserved(HashSet<string> names, List<(int First, int Last)> ranges, bool enumeration = false)
        {
            var strings = Current.Kind == TokenKind.String;
            do
            {
                if (strings)
                {
                    Require(names.Add(Literal().Text), "protobuf.reserved", "A reserved name is duplicated.");
                }
                else
                {
                    var first = Integer(Constant());
                    var last = first;
                    if (Take("to"))
                    {
                        last = Take("max") ? enumeration ? int.MaxValue : 536870911 : Integer(Constant());
                    }

                    Require(first >= (enumeration ? int.MinValue : 1) &&
                        last <= (enumeration ? int.MaxValue : 536870911) && first <= last,
                        "protobuf.reserved", "The reserved number range is invalid.");
                    ranges.Add(((int)first, (int)last));
                }
            }
            while (Take(","));
            Expect(";");
        }

        private void Service()
        {
            Identifier();
            Expect("{");
            var methods = new HashSet<string>(StringComparer.Ordinal);
            while (!Take("}"))
            {
                if (Take(";")) { continue; }
                if (Take("option")) { Option(); continue; }
                Expect("rpc");
                Require(methods.Add(Identifier()), "protobuf.duplicate", "RPC names must be unique.");
                Expect("("); Take("stream");
                Reference(TypeName(), _package, true);
                Expect(")"); Expect("returns"); Expect("("); Take("stream");
                Reference(TypeName(), _package, true);
                Expect(")");
                if (Take("{"))
                {
                    while (!Take("}")) { Expect("option"); Option(); }
                }
                else { Expect(";"); }
            }
        }

        private void Option()
        {
            if (Current.Text == "(") { Unsupported("Custom options require an explicit extension policy."); }
            var option = TypeName();
            Expect("=");
            var value = Constant();
            Expect(";");
            if (option is "java_package" or "java_outer_classname" or "go_package" or "csharp_namespace" or
                "objc_class_prefix" or "php_namespace" or "php_metadata_namespace" or "ruby_package")
            {
                Require(value.Kind == TokenKind.String, "protobuf.option", "The language namespace option requires a string.");
            }
            else if (option is "deprecated" or "java_multiple_files" or "cc_generic_services" or
                "java_generic_services" or "py_generic_services" or "cc_enable_arenas")
            {
                Require(value.Text is "true" or "false", "protobuf.option", "The option requires a boolean.");
            }
            else if (option == "optimize_for")
            {
                Require(value.Text is "SPEED" or "CODE_SIZE" or "LITE_RUNTIME", "protobuf.option", "Invalid optimization mode.");
            }
            else { Unsupported("This Protobuf option has no implemented validation policy."); }
        }

        private void Reference(string type, string scope, bool messageOnly = false)
        {
            if (Scalars.Contains(type))
            {
                Require(!messageOnly, "protobuf.type", "RPC arguments require message types.");
            }
            else { owner._references.Add((type, scope, messageOnly)); }
        }

        private int Tag()
        {
            var number = Integer(Constant());
            Require(number >= 1 && number <= 536870911 && (number < 19000 || number > 19999),
                "protobuf.tag", "The field number is invalid or reserved.");
            return (int)number;
        }

        private string TypeName()
        {
            var prefix = Take(".") ? "." : "";
            var name = prefix + Identifier();
            while (Take(".")) { name += "." + Identifier(); }
            return name;
        }

        private string Identifier()
        {
            Require(Current.Kind == TokenKind.Identifier, "protobuf.identifier", "An identifier is required.");
            return tokens[_index++].Text;
        }

        private Token Literal()
        {
            Require(Current.Kind == TokenKind.String, "protobuf.string", "A quoted string literal is required.");
            return tokens[_index++];
        }

        private Token Constant()
        {
            var sign = Take("-") ? "-" : Take("+") ? "+" : "";
            Require(Current.Kind is TokenKind.String or TokenKind.Identifier or TokenKind.Number,
                "protobuf.constant", "An option or numeric value is required.");
            var token = tokens[_index++];
            Require(sign.Length == 0 || token.Kind != TokenKind.String, "protobuf.constant", "A string cannot have a numeric sign.");
            return sign.Length == 0 ? token : token with { Text = sign + token.Text };
        }

        private bool Take(string value)
        {
            owner.Context.Work();
            if (Current.Text != value) { return false; }
            _index++;
            return true;
        }

        private void Expect(string value) => Require(Take(value), "protobuf.syntax", $"Expected '{value}'.");
        private static void Fail(string message) => ValidationContext.Fail(DocumentValidationStatus.Invalid, "$", "protobuf.syntax", message);
    }
}
