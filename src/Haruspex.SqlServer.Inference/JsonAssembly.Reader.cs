using System.Text.Json;
using Haruspex.Collections;
using Haruspex.Diagnostics;
using Haruspex.Ir;
using Haruspex.SqlServer.Inference.Lattice;

namespace Haruspex.SqlServer.Inference;

internal static partial class JsonAssembly
{
    /// <summary>One member of an object read so far: its key, its value, and whether it is always there.</summary>
    /// <param name="Name">The key, decoded; null when the key is built from data, so its name is not known.</param>
    /// <param name="Shape">The value's shape.</param>
    /// <param name="Presence">
    /// <see cref="IrPresence.Always"/>, or <see cref="IrPresence.OmittedWhenNull"/> for a member of <c>concat_ws</c> that is left
    /// out when its item is NULL.
    /// </param>
    /// <param name="Nulls">Why the value may be JSON null when the key is always there; why the key is left out otherwise.</param>
    private sealed record Member(string? Name, Shape Shape, IrPresence Presence, EquatableArray<IrNullReason> Nulls);

    /// <summary>Values of an array read so far that come together: one value, or the items of a <c>string_agg</c> or <c>concat_ws</c>.</summary>
    private readonly record struct Group(Shape Shape, EquatableArray<IrNullReason> Nulls, int Min, int? Max);

    /// <summary>
    /// Reads one sequence of pieces, left to right, as a parser reads text: literals character by character, and every other
    /// piece as the text it stands for. Arrays and objects may open in one piece and close in another, each inside the other,
    /// so the reader keeps a stack of those open (<see cref="Frame"/>).
    /// </summary>
    private sealed class Reader
    {
        /// <summary>What the sequence is read as.</summary>
        public enum Mode
        {
            /// <summary>Text read as JSON: one array or object.</summary>
            Document,

            /// <summary>An item of <c>string_agg</c> or <c>concat_ws</c> between an array's brackets: one JSON value.</summary>
            Value,

            /// <summary>An item of <c>string_agg</c> or <c>concat_ws</c> where an object's keys go: members, <c>"key":value</c>.</summary>
            Members,
        }

        /// <summary>How reading ended.</summary>
        public enum Outcome
        {
            /// <summary>Read: <see cref="Whole"/>, <see cref="Members"/> or <see cref="Empty"/> say what.</summary>
            Read,

            /// <summary>The text is not JSON on this way, so reading it is an error, and it describes no call that succeeds.</summary>
            NeverJson,

            /// <summary>The JSON is not known: <see cref="Why"/> says why.</summary>
            NotKnown,
        }

        private enum State
        {
            /// <summary>Before anything: the <c>[</c> or <c>{</c> that opens the text.</summary>
            Start,

            /// <summary>After <c>[</c>: a value or <c>]</c>.</summary>
            Open,

            /// <summary>A value: after a comma in an array, after a colon in an object, or the one value of an item.</summary>
            Value,

            /// <summary>After <c>{</c>: a key or <c>}</c>.</summary>
            Members,

            /// <summary>A key: after a comma in an object, or the first of an item's members.</summary>
            Key,

            /// <summary>After a key: <c>:</c>.</summary>
            Colon,

            /// <summary>After a value: <c>,</c> or the closing bracket.</summary>
            After,

            /// <summary>After <c>string_agg</c> or <c>concat_ws</c> that may have written nothing: the closing bracket only.</summary>
            Gap,

            /// <summary>Inside a string value's quotes.</summary>
            InString,

            /// <summary>Inside a key's quotes, the key built from pieces, so its name is not known.</summary>
            InKey,

            /// <summary>After the array or object that is the text.</summary>
            Done,
        }

        /// <summary>
        /// An array or object open in the text, or the whole of what is read (the root, which has no closing bracket).
        /// </summary>
        /// <param name="closer">Its closing bracket: <c>]</c>, <c>}</c>, or none for the root.</param>
        /// <param name="keyed">Whether it holds members rather than values: an object, or the root of an item of members.</param>
        private sealed class Frame(char closer, bool keyed, State state)
        {
            public char Closer { get; } = closer;

            public bool Keyed { get; } = keyed;

            public State State { get; set; } = state;

            /// <summary>An array's values, or an item's one value.</summary>
            public List<Group> Groups { get; } = [];

            /// <summary>An object's members.</summary>
            public List<Member> Members { get; } = [];

            /// <summary>The keys written so far, each once, on this way through the text.</summary>
            public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

            /// <summary>The key whose value comes next; null when it is built from data.</summary>
            public string? Key { get; set; }
        }

        private readonly List<TextPiece> sequence;
        private readonly SourceLocation at;
        private readonly Mode mode;
        private readonly Stack<Frame> frames = new();
        private readonly List<Shape> parts = [];
        private bool notJson;
        private string literalInString = "";
        private bool literalOnly = true;
        private Shape? listItem;
        private bool open;
        private bool endsInToken;

        public Reader(List<TextPiece> sequence, SourceLocation at, Mode mode)
        {
            this.sequence = sequence;
            this.at = at;
            this.mode = mode;
            frames.Push(mode switch
            {
                Mode.Document => new Frame('\0', keyed: false, State.Start),
                Mode.Value => new Frame('\0', keyed: false, State.Value),
                _ => new Frame('\0', keyed: true, State.Key),
            });
        }

        /// <summary>Whether the text opens an array or object across pieces, or is one JSON value assembled here: whether it is this idiom.</summary>
        public bool Assembled { get; private set; }

        /// <summary>Whether the text opens an array or object across pieces: whether it builds JSON as text here, rather than holding it whole.</summary>
        public bool Opened { get; private set; }

        /// <summary>Why the JSON is not known, when reading ended so.</summary>
        public UnknownShape? Why { get; private set; }

        /// <summary>The document read, or an item's one value.</summary>
        public Shape? Whole { get; private set; }

        /// <summary>Why an item's one value may be JSON null.</summary>
        public EquatableArray<IrNullReason> WholeNulls { get; private set; } = [];

        /// <summary>An item's members.</summary>
        public IReadOnlyList<Member> Members => Root.Members;

        /// <summary>For an item, whether its text is empty, or spaces only: it writes nothing.</summary>
        public bool Empty { get; private set; }

        /// <summary>For an item, whether its text may end in a number or a word that text after it would continue.</summary>
        public bool EndsOpen => open || endsInToken;

        private Frame Top => frames.Peek();

        private Frame Root => frames.Last();

        private State Now
        {
            get => Top.State;
            set => Top.State = value;
        }

        /// <summary>Whether a value goes next.</summary>
        private bool AtValue => Now is State.Open or State.Value;

        /// <summary>Whether a key goes next.</summary>
        private bool AtKey => Now is State.Members or State.Key;

        /// <summary>Reads the sequence: <see cref="Outcome"/> says how it ended.</summary>
        public Outcome Read()
        {
            for (var i = 0; i < sequence.Count; i++)
            {
                var piece = sequence[i];
                var going = endsInToken && piece is not LiteralPiece
                    ? Stop("a number or word is written partly in a literal and partly by a value", at)
                    : piece switch
                    {
                        LiteralPiece literal => Literal(literal, i == 0),
                        NumberPiece number => Number(number),
                        SafeTextPiece safe => Now is State.InString ? Part(safe.String, safe.At) : Now is State.InKey || Stop(safe.Unquoted, safe.At),
                        JsonPiece json => Json(json.Content, i == 0),
                        ListPiece list => List(list),
                        JoinedPiece joined => Joined(joined),
                        OpaquePiece { CutShort: true } cut => Stop(CutBefore(cut)),
                        OpaquePiece opaque => Stop(Now is State.InString or State.InKey ? Unquoted(opaque) : Unknown($"{opaque.What} is written into the JSON as text, which may not be one JSON value", opaque.At)),
                        _ => Stop("text this does not read is written into the JSON", at),
                    };
                if (!going)
                    return notJson ? Outcome.NeverJson : Outcome.NotKnown;
            }

            return mode switch
            {
                Mode.Document => Ended(),
                Mode.Value => ValueEnded(),
                _ => MembersEnded(),
            };
        }

        private Outcome Ended()
        {
            if (Now == State.Start)
                return NotKnown(Unknown("one way the text can be is empty", at));
            return frames.Count == 1 && Now == State.Done ? Outcome.Read : Outcome.NeverJson;
        }

        private Outcome ValueEnded()
        {
            if (frames.Count > 1)
                return NotKnown(Unknown("an item opens an array or object it does not close", at));
            if (Now == State.Value && Root.Groups.Count == 0)
            {
                Empty = true;
                return Outcome.Read;
            }

            if (Now == State.InString)
                return Outcome.NeverJson;
            if (Now == State.After && Root.Groups is [{ Min: 1, Max: 1 } only])
            {
                (Whole, WholeNulls) = (only.Shape, only.Nulls);
                return Outcome.Read;
            }

            return NotKnown(Unknown("an item may be more than one JSON value", at));
        }

        private Outcome MembersEnded()
        {
            if (frames.Count > 1)
                return NotKnown(Unknown("an item opens an array or object it does not close", at));
            Empty = Now == State.Key && Root.Members.Count == 0;
            return Empty || Now == State.After ? Outcome.Read : NotKnown(Unknown("an item is not whole members, each \"key\":value", at));
        }

        private Outcome NotKnown(UnknownShape why)
        {
            Why = why;
            return Outcome.NotKnown;
        }

        /// <summary>Stops reading: the JSON is not known, for this reason.</summary>
        private bool Stop(string why, SourceLocation where) => Stop(Unknown(why, where));

        private bool Stop(UnknownShape why)
        {
            Why = why;
            return false;
        }

        /// <summary>Stops reading: this text is not JSON, so reading it is an error, and it describes no call that succeeds.</summary>
        private bool NotJson()
        {
            notJson = true;
            return false;
        }

        /// <summary>Text that is not what goes next: not JSON in a document, and in an item, text this does not read as one.</summary>
        private bool Unexpected(SourceLocation where) =>
            mode == Mode.Document ? NotJson() : Stop("an item may be more than one JSON value", where);

        /// <summary>A value read where one goes: an array's next value, an object's value for its key, or the whole text.</summary>
        private bool Deliver(Shape shape, EquatableArray<IrNullReason> nulls)
        {
            open = false;
            if (frames.Count == 1 && mode == Mode.Document)
            {
                Whole = shape;
                Now = State.Done;
                return true;
            }

            if (Top.Keyed)
                Top.Members.Add(new Member(Top.Key, shape, IrPresence.Always, nulls));
            else
                Top.Groups.Add(new Group(shape, nulls, 1, 1));
            Now = State.After;
            return true;
        }

        /// <summary>An array or object opened by <paramref name="bracket"/> that a later piece closes.</summary>
        private void Push(char bracket) =>
            frames.Push(bracket == '[' ? new Frame(']', keyed: false, State.Open) : new Frame('}', keyed: true, State.Members));

        /// <summary>The array or object on top closed: its shape, as the value where it was opened.</summary>
        private bool Close()
        {
            var frame = frames.Pop();
            if (frame.Closer == ']')
                return Deliver(ArrayOf(frame.Groups), []);
            return ObjectOf(frame, out var shape) && Deliver(shape, []);
        }

        private static Shape ArrayOf(List<Group> groups)
        {
            if (groups.All(g => g is { Min: 1, Max: 1 }))
                return new TupleShape([.. groups.Select(g => new ElementShape(g.Shape, g.Nulls))]);
            var min = groups.Sum(g => g.Min);
            int? max = groups.Any(g => g.Max is null) ? null : groups.Sum(g => g.Max ?? 0);
            return new ArrayShape(
                groups.Select(g => g.Shape).Aggregate(JsonShapes.Join),
                groups.Select(g => g.Nulls).Aggregate((EquatableArray<IrNullReason>)[], NullReasons.Join),
                min > 0 ? min : null,
                max);
        }

        /// <summary>
        /// An object's shape: its keys, each with its value, or, when every key is built from data, an object whose keys are data
        /// with the values of all of them (observed: Keys_built_from_data_may_repeat_and_are_kept_as_written; a reader keeps one
        /// of the values of a key written twice, and each is one of these). Keys of both kinds can't be said, so they stop
        /// (docs/decisions/0087-keys-built-from-data-make-an-object-whose-keys-are-data.md).
        /// </summary>
        private bool ObjectOf(Frame frame, out Shape shape)
        {
            shape = AnyString;
            if (frame.Members.Count > 0 && frame.Members.All(m => m.Name is null))
            {
                shape = new RecordShape(
                    frame.Members.Select(m => m.Shape).Aggregate(JsonShapes.Join),
                    frame.Members.Where(m => m.Presence == IrPresence.Always).Select(m => m.Nulls).Aggregate((EquatableArray<IrNullReason>)[], NullReasons.Join));
                return true;
            }

            if (frame.Members.Any(m => m.Name is null))
                return Stop("a key is built from data beside keys written in literals, so the object is neither one of known keys nor one whose keys are all data", at);
            shape = new ObjectShape([.. frame.Members.Select(m => m is { Name: { } name } ? new FieldShape(name, m.Shape, m.Presence, m.Nulls) : null).OfType<FieldShape>()]) { Writers = KeyWriters.Literal };
            return true;
        }

        private bool Part(Shape shape, SourceLocation where)
        {
            if (listItem is not null)
                return Stop("text is written beside a string_agg inside the same quotes", where);
            parts.Add(shape);
            literalOnly = false;
            return true;
        }

        private bool Literal(LiteralPiece literal, bool first)
        {
            var text = literal.Text;
            if (open && text.Length > 0 && (char.IsAsciiLetterOrDigit(text[0]) || text[0] is '.' or '-' or '+'))
                return Stop("a number or word is followed by text that continues it", literal.At);
            open = false;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (Now is State.InString or State.InKey)
                {
                    var close = text.IndexOf('"', i);
                    var inner = close < 0 ? text[i..] : text[i..close];
                    if (inner.Any(ch => ch is '\\' or < ' '))
                        return Stop("a literal inside quotes holds an escape or a control character", literal.At);
                    if (Now == State.InString && inner.Length > 0)
                    {
                        if (listItem is not null)
                            return Stop("text is written beside a string_agg inside the same quotes", literal.At);
                        literalInString += inner;
                        parts.Add(LiteralString(inner, literal.National, literal.At));
                    }

                    if (close < 0)
                        return true;
                    if (Now == State.InString)
                    {
                        CloseString(literal);
                    }
                    else
                    {
                        Top.Key = null;
                        Now = State.Colon;
                    }

                    i = close + 1;
                    continue;
                }

                if (IsSpace(c))
                {
                    i++;
                    continue;
                }

                switch (Now)
                {
                    case State.Start:
                        // Not an array or object: an error for json_query, but text such as '' or '1,2' put in brackets later
                        // would be JSON of another shape, so it is not dropped as one that describes no call.
                        if (c is not ('[' or '{'))
                            return Stop("one way the text can be is not an array or object", literal.At);
                        if (End(text, i) is { } whole)
                        {
                            if (!Parsed(text[i..whole], literal, out var parsed))
                                return false;
                            Deliver(parsed, []);
                            i = whole;
                            continue;
                        }

                        Assembled = first;
                        Opened = first;
                        Push(c);
                        i++;
                        continue;
                    case State.Done:
                        return Stop("text follows the array or object, which json_query would ignore", literal.At);
                    case State.Open or State.Members or State.Gap or State.After when c == Top.Closer:
                        if (!Close())
                            return false;
                        i++;
                        continue;
                    case State.Gap:
                        return Stop("concat_ws or string_agg, which may write nothing, is followed by more than the closing bracket", literal.At);
                    case State.After when c == ',' && (Top.Closer != '\0' || Top.Keyed):
                        Now = Top.Keyed ? State.Key : State.Value;
                        i++;
                        continue;
                    case State.Open or State.Value when c == '"' && text.IndexOf('"', i + 1) < 0:
                    {
                        // A string the literal opens and a later piece closes.
                        var inner = text[(i + 1)..];
                        if (inner.Any(ch => ch is '\\' or < ' '))
                            return Stop("a literal inside quotes holds an escape or a control character", literal.At);
                        Now = State.InString;
                        literalInString = inner;
                        literalOnly = true;
                        parts.Clear();
                        if (inner.Length > 0)
                            parts.Add(LiteralString(inner, literal.National, literal.At));
                        return true;
                    }

                    case State.Open or State.Value:
                    {
                        var stop = End(text, i);
                        if (stop is null && c is '[' or '{')
                        {
                            Push(c);
                            i++;
                            continue;
                        }

                        endsInToken = stop is null;
                        var token = text[i..(stop ?? text.Length)];
                        if (!Parsed(token, literal, out var element))
                            return false;
                        Deliver(element, token == "null" ? [new IrNullReason(IrNullRule.NullLiteral, literal.At.ToString())] : []);
                        i += token.Length;
                        continue;
                    }

                    case State.Members or State.Key when c == '"':
                    {
                        if (End(text, i) is not { } end)
                        {
                            // A key the literal opens and a later piece closes: built from data.
                            if (text[(i + 1)..].Any(ch => ch is '\\' or < ' '))
                                return Stop("a literal inside quotes holds an escape or a control character", literal.At);
                            Now = State.InKey;
                            return true;
                        }

                        if (!Named(text[i..end], literal))
                            return false;
                        Now = State.Colon;
                        i = end;
                        continue;
                    }

                    case State.Colon when c == ':':
                        Now = State.Value;
                        i++;
                        continue;
                    default:
                        return Unexpected(literal.At);
                }
            }

            return true;
        }

        /// <summary>
        /// A key written whole in a literal, <paramref name="quoted"/> with its quotes: its name, decoded, as the next value's
        /// key; false, having said why, when it is not read or may be written twice.
        /// </summary>
        private bool Named(string quoted, LiteralPiece literal)
        {
            string? name;
            try
            {
                using var document = JsonDocument.Parse(quoted);
                name = document.RootElement.GetString();
            }
            catch (JsonException)
            {
                return NotJson();
            }

            if (name is null)
                return NotJson();

            if (!literal.National && name.Any(c => c > '\u007f'))
                return Stop("a key in a literal without N is not ASCII, which the database's code page may change", literal.At);
            if (!Top.Keys.Add(name))
                return Stop($"the key \"{name}\" is written twice, and readers differ on which of its values they keep", literal.At);
            Top.Key = name;
            return true;
        }

        /// <summary>
        /// Where the JSON value starting at <paramref name="start"/> ends, when it ends within the text: a string after its
        /// closing quote, an array or object after its closing bracket, anything else before a comma, a bracket or a space. Null
        /// when the text ends first.
        /// </summary>
        private static int? End(string text, int start)
        {
            var c = text[start];
            if (c == '"')
            {
                for (var i = start + 1; i < text.Length; i++)
                {
                    if (text[i] == '\\')
                        i++;
                    else if (text[i] == '"')
                        return i + 1;
                }

                return null;
            }

            if (c is '[' or '{')
            {
                var depth = 0;
                var inString = false;
                for (var i = start; i < text.Length; i++)
                {
                    var d = text[i];
                    if (inString)
                    {
                        if (d == '\\')
                            i++;
                        else if (d == '"')
                            inString = false;
                    }
                    else if (d == '"')
                    {
                        inString = true;
                    }
                    else if (d is '[' or '{')
                    {
                        depth++;
                    }
                    else if (d is ']' or '}' && --depth == 0)
                    {
                        return i + 1;
                    }
                }

                return null;
            }

            for (var i = start; i < text.Length; i++)
            {
                if (text[i] is ',' or ']' or '}' || IsSpace(text[i]))
                    return i;
            }

            return null;
        }

        /// <summary>A whole JSON value written in a literal: its shape; false, having said why, when it is not read or not JSON.</summary>
        private bool Parsed(string value, LiteralPiece literal, out Shape shape)
        {
            shape = AnyString;
            var wrapped = $"[{value}]";
            if (!JsonLiterals.IsDocumentText(wrapped))
                return NotJson();
            switch (JsonLiterals.Parse(wrapped, literal.National, literal.At))
            {
                case TupleShape { Items: [var only] }:
                    shape = only.Type;
                    return true;
                case UnknownShape why:
                    return Stop(why);
                default:
                    return NotJson();
            }
        }

        private void CloseString(LiteralPiece closing)
        {
            if (listItem is { } items)
            {
                Top.Groups.Add(new Group(items, [], 1, null));
                Now = State.After;
                open = false;
            }
            else
            {
                Deliver(parts.Count == 1 ? parts[0] : literalOnly ? LiteralString(literalInString, closing.National, closing.At) : AnyString, []);
            }

            listItem = null;
            literalInString = "";
            literalOnly = true;
            parts.Clear();
        }

        private bool Number(NumberPiece number)
        {
            switch (Now)
            {
                case State.InString:
                    return Part(number.Text, at);
                case State.InKey:
                    return true;
                case State.Open or State.Value:
                    Deliver(number.Number, []);
                    open = true;
                    return true;
                case State.After:
                    return Continues();
                default:
                    return Stop("a number is written where no value goes", at);
            }
        }

        private bool Json(Shape content, bool first)
        {
            switch (Now)
            {
                case State.Start when first && sequence.Count == 1:
                    Assembled = true;
                    return Deliver(content, []);
                case State.Open or State.Value:
                    return Deliver(content, []);
                case State.InString or State.InKey:
                    return Stop("JSON is written inside quotes, where it is not escaped", at);
                case State.After:
                    return NotJson();
                default:
                    return Stop("JSON is written where no value goes", at);
            }
        }

        private bool List(ListPiece list)
        {
            if (Now == State.InString)
            {
                // ["…" + string_agg(x, '","') + "…"]: each item a string, the separator closing one and opening the next.
                if (Top.Keyed || parts.Count > 0 || listItem is not null || !IsSeparator(list.Separator, quoted: true))
                    return Stop("string_agg inside quotes is not the only text in an array's string there, or its separator is not \",\"", at);
                if (StringItem(list.Item, at, out var why) is not { } inQuotes)
                    return why is null ? NotJson() : Stop(why);
                listItem = inQuotes;
                literalOnly = false;
                return true;
            }

            if (Now == State.After)
                return Continues();
            if (!(Top.Keyed ? AtKey : AtValue))
                return Stop("string_agg is written where no value or key goes", at);
            if (!IsSeparator(list.Separator, quoted: false))
                return Stop("string_agg's separator is not a literal comma", at);
            if (Top.Keyed)
                return MembersListed(list);
            if (Item(list.Item, at, out var unknownItem) is not { } item)
                return Stop(unknownItem ?? Unknown("an item has no text this reads", at));
            if (item.Shape is not null)
                Top.Groups.Add(new Group(item.Shape, item.Nulls, item.MayBeEmpty ? 0 : 1, null));
            else if (!item.MayBeEmpty)
                return NotJson();
            return Written(mayWriteNothing: item.MayBeEmpty || item.Shape is null, item.EndsOpen, "string_agg");
        }

        /// <summary>
        /// <c>string_agg</c> where an object's keys go: a member for each row, so a key is written once per row and may be written
        /// twice, unless it is built from data, when the object's keys are data (<see cref="ObjectOf"/>).
        /// </summary>
        private bool MembersListed(ListPiece list)
        {
            if (MembersItem(list.Item, at, out var why) is not { } item)
                return Stop(why ?? Unknown("an item has no text this reads", at));
            if (item.Members.FirstOrDefault(m => m.Name is not null) is { Name: { } name })
                return Stop($"string_agg writes the key \"{name}\" for each row, so it may be written twice, and readers differ on which of its values they keep", at);
            if (item.Members.Count == 0 && !item.MayBeEmpty)
                return NotJson();
            Top.Members.AddRange(item.Members);
            return Written(mayWriteNothing: item.MayBeEmpty || item.Members.Count == 0, item.EndsOpen, "string_agg");
        }

        /// <summary>
        /// The state after a <c>string_agg</c> or <c>concat_ws</c>: after a value, or, when it may write nothing, where only the
        /// closing bracket may follow, since anything else would follow the comma or bracket before it.
        /// </summary>
        private bool Written(bool mayWriteNothing, bool endsOpen, string what)
        {
            open = endsOpen;
            if (!mayWriteNothing)
            {
                Now = State.After;
                return true;
            }

            if (Now is State.Value or State.Key)
                return Stop($"{what}, which may write nothing, follows a comma", at);
            Now = State.Gap;
            return true;
        }

        /// <summary>
        /// A value written straight after another: not JSON, unless the one before may end in a number or a word, which the
        /// server reads on into the next (observed: A_number_written_straight_after_a_number_continues_it), so it is not known.
        /// </summary>
        private bool Continues() => open || endsInToken ? Stop("a value is written straight after one that may end in a number, which it would continue", at) : NotJson();

        private bool Joined(JoinedPiece joined)
        {
            if (!(Top.Keyed ? AtKey : AtValue) || !IsSeparator(joined.Separator, quoted: false))
                return Stop("concat_ws is not the whole of the array or object between its brackets, or its separator is not a literal comma", at);
            return Top.Keyed ? JoinedMembers(joined) : JoinedValues(joined);
        }

        private bool JoinedValues(JoinedPiece joined)
        {
            // An item NULL is left out; an item of empty text is not, but alone it leaves [] (observed:
            // An_empty_item_alone_between_the_brackets_leaves_an_empty_array), so either may be absent.
            var shapes = new List<(Shape Shape, EquatableArray<IrNullReason> Nulls)>();
            var min = 0;
            var endsOpen = false;
            foreach (var argument in joined.Items)
            {
                if (Item(argument.Pieces, at, out var why) is not { } item)
                    return Stop(why ?? Unknown("an item has no text this reads", at));
                if (item.Shape is not null)
                    shapes.Add((item.Shape, item.Nulls));
                else if (!argument.MayBeAbsent && !item.MayBeEmpty)
                    return NotJson();
                if (!argument.MayBeAbsent && !item.MayBeEmpty)
                    min++;
                endsOpen |= item.EndsOpen;
            }

            if (shapes.Count > 0)
                Top.Groups.Add(new Group(shapes.Select(s => s.Shape).Aggregate(JsonShapes.Join), shapes.Select(s => s.Nulls).Aggregate(NullReasons.Join), min, shapes.Count));
            return Written(mayWriteNothing: min == 0, endsOpen, "concat_ws");
        }

        /// <summary>
        /// <c>concat_ws</c> where an object's keys go: each item's members, those of an item that may be NULL left out with it
        /// (observed: Concat_ws_of_members_leaves_out_those_that_are_null_and_a_stray_comma_is_not_json), which the IR says as a
        /// key left out when null, for the item's reasons. A key that may be JSON null too, or an item that may be empty text,
        /// can't be said so, and stops.
        /// </summary>
        private bool JoinedMembers(JoinedPiece joined)
        {
            var mayWriteNothing = true;
            var endsOpen = false;
            foreach (var argument in joined.Items)
            {
                if (MembersItem(argument.Pieces, at, out var why) is not { } item)
                    return Stop(why ?? Unknown("an item has no text this reads", at));
                if (item.Members.Count == 0)
                {
                    if (!argument.MayBeAbsent && !item.MayBeEmpty)
                        return NotJson();
                    continue;
                }

                if (item.MayBeEmpty)
                    return Stop("an item of concat_ws may be empty text, which leaves its keys out where no NULL does, so whether they are there is not known", at);
                foreach (var written in item.Members)
                {
                    var member = written;
                    if (argument.MayBeAbsent)
                    {
                        if (member.Presence != IrPresence.Always || member.Nulls.Count > 0)
                            return Stop($"the key \"{member.Name}\" may be JSON null and may be left out, which one presence can't say", at);
                        member = member with { Presence = IrPresence.OmittedWhenNull, Nulls = argument.AbsentBecause };
                    }

                    if (member.Name is { } name && !Top.Keys.Add(name))
                        return Stop($"the key \"{name}\" is written twice, and readers differ on which of its values they keep", at);
                    Top.Members.Add(member);
                }

                mayWriteNothing &= argument.MayBeAbsent;
                endsOpen |= item.EndsOpen;
            }

            return Written(mayWriteNothing, endsOpen, "concat_ws");
        }

        /// <summary>Whether a separator is a literal comma, with spaces around it, or, inside quotes, <c>","</c>.</summary>
        private static bool IsSeparator(EquatableArray<TextPiece> separator, bool quoted) =>
            separator is [LiteralPiece { Text: var text }]
            && (quoted
                ? text.Length >= 3 && text[0] == '"' && text[^1] == '"' && text[1..^1].Trim(' ', '\t', '\n', '\r') == ","
                : text.Trim(' ', '\t', '\n', '\r') == ",");
    }
}
