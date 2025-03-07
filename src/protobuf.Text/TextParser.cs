#region Copyright notice and license
// Protocol Buffers - Google's data interchange format
// Copyright 2015 Google Inc.  All rights reserved.
// https://developers.google.com/protocol-buffers/
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//     * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//     * Neither the name of Google Inc. nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
#endregion

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Protobuf.Text
{
    /// <summary>
    /// Reflection-based converter from text to messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instances of this class are thread-safe, with no mutable state.
    /// </para>
    /// <para>
    /// This is a simple start to get text parsing working. As it's reflection-based,
    /// it's not as quick as baking calls into generated messages - but is a simpler implementation.
    /// (This code is generally not heavily optimized.)
    /// </para>
    /// </remarks>
    public sealed class TextParser
    {
        // Note: using 0-9 instead of \d to ensure no non-ASCII digits.
        // This regex isn't a complete validator, but will remove *most* invalid input. We rely on parsing to do the rest.
        private static readonly Regex TimestampRegex = new Regex(@"^(?<datetime>[0-9]{4}-[01][0-9]-[0-3][0-9]T[012][0-9]:[0-5][0-9]:[0-5][0-9])(?<subseconds>\.[0-9]{1,9})?(?<offset>(Z|[+-][0-1][0-9]:[0-5][0-9]))$", RegexOptions.None);
        private static readonly Regex DurationRegex = new Regex(@"^(?<sign>-)?(?<int>[0-9]{1,12})(?<subseconds>\.[0-9]{1,9})?s$", RegexOptions.None);
        private static readonly int[] SubsecondScalingFactors = { 0, 100000000, 100000000, 10000000, 1000000, 100000, 10000, 1000, 100, 10, 1 };
        private static readonly char[] FieldMaskPathSeparators = new[] { ',' };

        private static readonly TextParser defaultInstance = new TextParser(Settings.Default);

        // TODO: Consider introducing a class containing parse state of the parser, tokenizer and depth. That would simplify these handlers
        // and the signatures of various methods.
        private static readonly Dictionary<string, Action<TextParser, IMessage, TextTokenizer>>
            WellKnownTypeHandlers = new Dictionary<string, Action<TextParser, IMessage, TextTokenizer>>
        {
            { Timestamp.Descriptor.FullName, (parser, message, tokenizer) => MergeTimestamp(message, tokenizer.Next()) },
            { Duration.Descriptor.FullName, (parser, message, tokenizer) => MergeDuration(message, tokenizer.Next()) },
            { Value.Descriptor.FullName, (parser, message, tokenizer) => parser.MergeStructValue(message, tokenizer) },
            { ListValue.Descriptor.FullName, (parser, message, tokenizer) =>
                parser.MergeRepeatedField(message, message.Descriptor.Fields[ListValue.ValuesFieldNumber], tokenizer) },
            { Struct.Descriptor.FullName, (parser, message, tokenizer) => parser.MergeStruct(message, tokenizer) },
            { Any.Descriptor.FullName, (parser, message, tokenizer) => parser.MergeAny(message, tokenizer) },
            { FieldMask.Descriptor.FullName, (parser, message, tokenizer) => MergeFieldMask(message, tokenizer.Next()) },
            { Int32Value.Descriptor.FullName, MergeWrapperField },
            { Int64Value.Descriptor.FullName, MergeWrapperField },
            { UInt32Value.Descriptor.FullName, MergeWrapperField },
            { UInt64Value.Descriptor.FullName, MergeWrapperField },
            { FloatValue.Descriptor.FullName, MergeWrapperField },
            { DoubleValue.Descriptor.FullName, MergeWrapperField },
            { BytesValue.Descriptor.FullName, MergeWrapperField },
            { StringValue.Descriptor.FullName, MergeWrapperField },
            { BoolValue.Descriptor.FullName, MergeWrapperField }
        };

        // Convenience method to avoid having to repeat the same code multiple times in the above
        // dictionary initialization.
        private static void MergeWrapperField(TextParser parser, IMessage message, TextTokenizer tokenizer)
        {
            parser.MergeField(message, message.Descriptor.Fields[WrappersReflection.WrapperValueFieldNumber], tokenizer);
        }

        /// <summary>
        /// Returns a formatter using the default settings.
        /// </summary>
        public static TextParser Default { get { return defaultInstance; } }

        private readonly Settings settings;

        /// <summary>
        /// Creates a new formatted with the given settings.
        /// </summary>
        /// <param name="settings">The settings.</param>
        public TextParser(Settings settings)
        {
            this.settings = settings;
        }

        /// <summary>
        /// Parses <paramref name="text"/> and merges the information into the given message.
        /// </summary>
        /// <param name="message">The message to merge the text information into.</param>
        /// <param name="text">The text to parse.</param>
        public void Merge(IMessage message, string text)
        {
            Merge(message, new StringReader(text));
        }

        /// <summary>
        /// Parses text read from <paramref name="textReader"/> and merges the information into the given message.
        /// </summary>
        /// <param name="message">The message to merge the text information into.</param>
        /// <param name="textReader">Reader providing the text to parse.</param>
        public void Merge(IMessage message, TextReader textReader)
        {
            var tokenizer = TextTokenizer.FromTextReader(textReader);
            Merge(message, tokenizer);
            var lastToken = tokenizer.Next();
            if (lastToken != TextToken.EndDocument)
            {
                throw new InvalidTextProtocolBufferException("Expected end of text after object");
            }
        }

        /// <summary>
        /// Merges the given message using data from the given tokenizer. In most cases, the next
        /// token should be a "start object" token, but wrapper types and nullity can invalidate
        /// that assumption. This is implemented as an LL(1) recursive descent parser over the stream
        /// of tokens provided by the tokenizer. This token stream is assumed to be valid text, with the
        /// tokenizer performing that validation - but not every token stream is valid "protobuf text".
        /// </summary>
        private void Merge(IMessage message, TextTokenizer tokenizer)
        {
            if (tokenizer.ObjectDepth > settings.RecursionLimit)
            {
                throw InvalidTextProtocolBufferException.JsonRecursionLimitExceeded();
            }
            if (message.Descriptor.IsWellKnownType())
            {
                Action<TextParser, IMessage, TextTokenizer> handler;
                if (WellKnownTypeHandlers.TryGetValue(message.Descriptor.FullName, out handler))
                {
                    handler(this, message, tokenizer);
                    return;
                }
                // Well-known types with no special handling continue in the normal way.
            }

            var token = tokenizer.Next();
            
            if (token.Type != TokenType.StartObject)
            {
                throw new InvalidTextProtocolBufferException("Expected an object");
            }

            var descriptor = message.Descriptor;
            var jsonFieldMap = descriptor.Fields.ByJsonName();
            // All the oneof fields we've already accounted for - we can only see each of them once.
            // The set is created lazily to avoid the overhead of creating a set for every message
            // we parsed, when oneofs are relatively rare.
            HashSet<OneofDescriptor> seenOneofs = null;

            while (true)
            {
                token = tokenizer.Next();

                if (tokenizer.ObjectDepth == 1 && token.Type == TokenType.EndDocument)
                {
                    return;
                }

                if (token.Type == TokenType.EndObject)
                {
                    return;
                }
                if (token.Type != TokenType.Name)
                {
                    throw new InvalidOperationException("Unexpected token type " + token.Type);
                }
                string name = token.StringValue;

                FieldDescriptor field;

                if (jsonFieldMap.TryGetValue(name, out field))
                {
                    if (field.ContainingOneof != null)
                    {
                        if (seenOneofs == null)
                        {
                            seenOneofs = new HashSet<OneofDescriptor>();
                        }
                        if (!seenOneofs.Add(field.ContainingOneof))
                        {
                            throw new InvalidTextProtocolBufferException($"Multiple values specified for oneof {field.ContainingOneof.Name}");
                        }
                    }

                    MergeField(message, field, tokenizer);
                }
                else
                {
                    if (settings.IgnoreUnknownFields)
                    {
                        tokenizer.SkipValue();
                    }
                    else
                    {
                        throw new InvalidTextProtocolBufferException("Unknown field: " + name);
                    }
                }
            }
        }

        private void MergeField(IMessage message, FieldDescriptor field, TextTokenizer tokenizer)
        {
            var token = tokenizer.Next();

            if (token.Type == TokenType.Null)
            {
                // Clear the field if we see a null token, unless it's for a singular field of type
                // google.protobuf.Value.
                // Note: different from Java API, which just ignores it.
                // TODO: Bring it more in line? Discuss...
                if (field.IsMap || field.IsRepeated || !IsGoogleProtobufValueField(field))
                {
                    field.Accessor.Clear(message);
                    return;
                }
            }

            tokenizer.PushBack(token);

            if (field.IsMap)
            {
                MergeMapField(message, field, tokenizer);
            }
            else if (field.IsRepeated)
            {
                MergeRepeatedField(message, field, tokenizer);
            }
            else
            {
                var value = ParseSingleValue(field, tokenizer);
                field.Accessor.SetValue(message, value);
            }
        }

        private void MergeRepeatedField(IMessage message, FieldDescriptor field, TextTokenizer tokenizer)
        {
            var token = tokenizer.Next();
            var flatRepeatedMode = false;

            if (token.Type != TokenType.StartArray)
            {
                tokenizer.PushBack(token);
                flatRepeatedMode = true;
            }

            IList list = (IList) field.Accessor.GetValue(message);

            while (true)
            {
                token = tokenizer.Next();

                if (flatRepeatedMode)
                {
                    if (token.Type == TokenType.EndObject || token.Type == TokenType.EndDocument)
                    {
                        // end of the repeat
                        tokenizer.PushBack(token);
                        return;
                    }

                    if (token.Type == TokenType.Name)
                    {
                        if (!token.StringValue.Equals(field.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            // end of the repeat
                            tokenizer.PushBack(token);
                            return;
                        }
                        // read the start of the object
                        token = tokenizer.Next();
                    }
                }
                else
                {
                    if (token.Type == TokenType.EndArray)
                    {
                        return;
                    }
                }

                tokenizer.PushBack(token);

                object value = ParseSingleValue(field, tokenizer);

                if (value == null)
                {
                    throw new InvalidTextProtocolBufferException("Repeated field elements cannot be null");
                }
                list.Add(value);
            }
        }

        private void MergeMapField(IMessage message, FieldDescriptor field, TextTokenizer tokenizer)
        {
            // Map fields are always objects, even if the values are well-known types: ParseSingleValue handles those.
            var token = tokenizer.Next();
            if (token.Type != TokenType.StartObject)
            {
                throw new InvalidTextProtocolBufferException("Expected an object to populate a map");
            }

            var type = field.MessageType;
            var keyField = type.FindFieldByNumber(1);
            var valueField = type.FindFieldByNumber(2);
            if (keyField == null || valueField == null)
            {
                throw new InvalidTextProtocolBufferException("Invalid map field: " + field.FullName);
            }
            IDictionary dictionary = (IDictionary) field.Accessor.GetValue(message);

            while (true)
            {
                token = tokenizer.Next();

                if (token.Type == TokenType.EndObject || token.Type == TokenType.EndDocument)
                {
                    return;
                }

                object key = ParseMapKey(keyField, token.StringValue);
                object value = ParseSingleValue(valueField, tokenizer);
                if (value == null)
                {
                    throw new InvalidTextProtocolBufferException("Map values must not be null");
                }
                dictionary[key] = value;
            }
        }

        private static bool IsGoogleProtobufValueField(FieldDescriptor field)
        {
            return field.FieldType == FieldType.Message &&
                field.MessageType.FullName == Value.Descriptor.FullName;
        }

        private object ParseSingleValue(FieldDescriptor field, TextTokenizer tokenizer)
        {
            var token = tokenizer.Next();
            
            if (token.Type == TokenType.Null)
            {
                // TODO: In order to support dynamic messages, we should really build this up
                // dynamically.
                if (IsGoogleProtobufValueField(field))
                {
                    return Value.ForNull();
                }
                return null;
            }

            var fieldType = field.FieldType;
            if (fieldType == FieldType.Message)
            {
                // Parse wrapper types as their constituent types.
                // TODO: What does this mean for null?
                if (field.MessageType.IsWrapperType())
                {
                    field = field.MessageType.Fields[WrappersReflection.WrapperValueFieldNumber];
                    fieldType = field.FieldType;
                }
                else
                {
                    // TODO: Merge the current value in message? (Public API currently doesn't make this relevant as we don't expose merging.)
                    tokenizer.PushBack(token);
                    IMessage subMessage = NewMessageForField(field);
                    Merge(subMessage, tokenizer);
                    return subMessage;
                }
            }

            switch (token.Type)
            {
                case TokenType.True:
                case TokenType.False:
                    if (fieldType == FieldType.Bool)
                    {
                        return token.Type == TokenType.True;
                    }
                    // Fall through to "we don't support this type for this case"; could duplicate the behaviour of the default
                    // case instead, but this way we'd only need to change one place.
                    goto default;
                case TokenType.StringValue:
                    return ParseSingleStringValue(field, token.StringValue);
                // Note: not passing the number value itself here, as we may end up storing the string value in the token too.
                case TokenType.Number:
                    return ParseSingleNumberValue(field, token);
                case TokenType.Null:
                    throw new NotImplementedException("Haven't worked out what to do for null yet");
                default:
                    throw new InvalidTextProtocolBufferException("Unsupported text token type " + token.Type + " for field type " + fieldType);
            }
        }

        /// <summary>
        /// Parses <paramref name="text"/> into a new message.
        /// </summary>
        /// <typeparam name="T">The type of message to create.</typeparam>
        /// <param name="text">The text to parse.</param>
        /// <exception cref="InvalidTextException">The text does not comply with RFC 7159</exception>
        /// <exception cref="InvalidTextProtocolBufferException">The text does not represent a Protocol Buffers message correctly</exception>
        public T Parse<T>(string text) where T : IMessage, new()
        {
            ProtoPreconditions.CheckNotNull(text, nameof(text));
            return Parse<T>(new StringReader(text));
        }

        /// <summary>
        /// Parses text read from <paramref name="textReader"/> into a new message.
        /// </summary>
        /// <typeparam name="T">The type of message to create.</typeparam>
        /// <param name="textReader">Reader providing the text to parse.</param>
        /// <exception cref="InvalidTextException">The text does not comply with RFC 7159</exception>
        /// <exception cref="InvalidTextProtocolBufferException">The text does not represent a Protocol Buffers message correctly</exception>
        public T Parse<T>(TextReader textReader) where T : IMessage, new()
        {
            ProtoPreconditions.CheckNotNull(textReader, nameof(textReader));
            T message = new T();
            Merge(message, textReader);
            return message;
        }

        /// <summary>
        /// Parses <paramref name="text"/> into a new message.
        /// </summary>
        /// <param name="text">The text to parse.</param>
        /// <param name="descriptor">Descriptor of message type to parse.</param>
        /// <exception cref="InvalidTextException">The text does not comply with RFC 7159</exception>
        /// <exception cref="InvalidTextProtocolBufferException">The text does not represent a Protocol Buffers message correctly</exception>
        public IMessage Parse(string text, MessageDescriptor descriptor)
        {
            ProtoPreconditions.CheckNotNull(text, nameof(text));
            ProtoPreconditions.CheckNotNull(descriptor, nameof(descriptor));
            return Parse(new StringReader(text), descriptor);
        }

        /// <summary>
        /// Parses text read from <paramref name="textReader"/> into a new message.
        /// </summary>
        /// <param name="textReader">Reader providing the text to parse.</param>
        /// <param name="descriptor">Descriptor of message type to parse.</param>
        /// <exception cref="InvalidTextException">The text does not comply with RFC 7159</exception>
        /// <exception cref="InvalidTextProtocolBufferException">The text does not represent a Protocol Buffers message correctly</exception>
        public IMessage Parse(TextReader textReader, MessageDescriptor descriptor)
        {
            ProtoPreconditions.CheckNotNull(textReader, nameof(textReader));
            ProtoPreconditions.CheckNotNull(descriptor, nameof(descriptor));
            IMessage message = descriptor.Parser.CreateTemplate();
            Merge(message, textReader);
            return message;
        }

        private void MergeStructValue(IMessage message, TextTokenizer tokenizer)
        {
            var firstToken = tokenizer.Next();
            var fields = message.Descriptor.Fields;
            switch (firstToken.Type)
            {
                case TokenType.Null:
                    fields[Value.NullValueFieldNumber].Accessor.SetValue(message, 0);
                    return;
                case TokenType.StringValue:
                    fields[Value.StringValueFieldNumber].Accessor.SetValue(message, firstToken.StringValue);
                    return;
                case TokenType.Number:
                    fields[Value.NumberValueFieldNumber].Accessor.SetValue(message, firstToken.NumberValue);
                    return;
                case TokenType.False:
                case TokenType.True:
                    fields[Value.BoolValueFieldNumber].Accessor.SetValue(message, firstToken.Type == TokenType.True);
                    return;
                case TokenType.StartObject:
                    {
                        var field = fields[Value.StructValueFieldNumber];
                        var structMessage = NewMessageForField(field);
                        tokenizer.PushBack(firstToken);
                        Merge(structMessage, tokenizer);
                        field.Accessor.SetValue(message, structMessage);
                        return;
                    }
                case TokenType.StartArray:
                    {
                        var field = fields[Value.ListValueFieldNumber];
                        var list = NewMessageForField(field);
                        tokenizer.PushBack(firstToken);
                        Merge(list, tokenizer);
                        field.Accessor.SetValue(message, list);
                        return;
                    }
                case TokenType.EndDocument:
                    return;
                default:
                    throw new InvalidOperationException("Unexpected token type: " + firstToken.Type);
            }
        }

        private void MergeStruct(IMessage message, TextTokenizer tokenizer)
        {
            var token = tokenizer.Next();
            if (token.Type != TokenType.StartObject)
            {
                throw new InvalidTextProtocolBufferException("Expected object value for Struct");
            }
            tokenizer.PushBack(token);

            var field = message.Descriptor.Fields[Struct.FieldsFieldNumber];
            MergeMapField(message, field, tokenizer);
        }

        private void MergeAny(IMessage message, TextTokenizer tokenizer)
        {
            // Record the token stream until we see the @type property. At that point, we can take the value, consult
            // the type registry for the relevant message, and replay the stream, omitting the @type property.
            var tokens = new List<TextToken>();

            var token = tokenizer.Next();
            if (token.Type != TokenType.StartObject)
            {
                throw new InvalidTextProtocolBufferException("Expected object value for Any");
            }
            int typeUrlObjectDepth = tokenizer.ObjectDepth;

            // The check for the property depth protects us from nested Any values which occur before the type URL
            // for *this* Any.
            while (token.Type != TokenType.Name ||
                token.StringValue != ProtobufAdapter.AnyTypeUrlField ||
                tokenizer.ObjectDepth != typeUrlObjectDepth)
            {
                tokens.Add(token);
                token = tokenizer.Next();

                if (tokenizer.ObjectDepth < typeUrlObjectDepth || token.Type == TokenType.EndDocument)
                {
                    throw new InvalidTextProtocolBufferException("Any message with no @type");
                }
            }

            // Don't add the @type property or its value to the recorded token list
            token = tokenizer.Next();
            if (token.Type != TokenType.StringValue)
            {
                throw new InvalidTextProtocolBufferException("Expected string value for Any.@type");
            }
            string typeUrl = token.StringValue;
            string typeName = Any.GetTypeName(typeUrl);

            MessageDescriptor descriptor = settings.TypeRegistry.Find(typeName);
            
            if (descriptor == null)
            {
                throw new InvalidOperationException($"Type registry has no descriptor for type name '{typeName}'");
            }

            // Now replay the token stream we've already read and anything that remains of the object, just parsing it
            // as normal. Our original tokenizer should end up at the end of the object.
            var replay = TextTokenizer.FromReplayedTokens(tokens, tokenizer);
            var body = descriptor.Parser.CreateTemplate();
            if (descriptor.IsWellKnownType())
            {
                MergeWellKnownTypeAnyBody(body, replay);
            }
            else
            {
                Merge(body, replay);
            }
            var data = body.ToByteString();

            // Now that we have the message data, we can pack it into an Any (the message received as a parameter).
            message.Descriptor.Fields[Any.TypeUrlFieldNumber].Accessor.SetValue(message, typeUrl);
            message.Descriptor.Fields[Any.ValueFieldNumber].Accessor.SetValue(message, data);
        }

        // Well-known types end up in a property called "value" in the text. As there's no longer a @type property
        // in the given text token stream, we should *only* have tokens of start-object, name("value"), the value
        // itself, and then end-object.
        private void MergeWellKnownTypeAnyBody(IMessage body, TextTokenizer tokenizer)
        {
            var token = tokenizer.Next(); // Definitely start-object; checked in previous method
            token = tokenizer.Next();
            // TODO: What about an absent Int32Value, for example?
            if (token.Type != TokenType.Name || token.StringValue != ProtobufAdapter.AnyWellKnownTypeValueField)
            {
                throw new InvalidTextProtocolBufferException($"Expected '{ProtobufAdapter.AnyWellKnownTypeValueField}' property for well-known type Any body");
            }
            Merge(body, tokenizer);
            token = tokenizer.Next();

            if (token.Type != TokenType.EndObject && token.Type != TokenType.EndDocument)
            {
                throw new InvalidTextProtocolBufferException($"Expected end-object token after @type/value for well-known type");
            }
        }

        #region Utility methods which don't depend on the state (or settings) of the parser.
        private static object ParseMapKey(FieldDescriptor field, string keyText)
        {
            switch (field.FieldType)
            {
                case FieldType.Bool:
                    if (keyText == "true")
                    {
                        return true;
                    }
                    if (keyText == "false")
                    {
                        return false;
                    }
                    throw new InvalidTextProtocolBufferException("Invalid string for bool map key: " + keyText);
                case FieldType.String:
                    return keyText;
                case FieldType.Int32:
                case FieldType.SInt32:
                case FieldType.SFixed32:
                    return ParseNumericString(keyText, int.Parse);
                case FieldType.UInt32:
                case FieldType.Fixed32:
                    return ParseNumericString(keyText, uint.Parse);
                case FieldType.Int64:
                case FieldType.SInt64:
                case FieldType.SFixed64:
                    return ParseNumericString(keyText, long.Parse);
                case FieldType.UInt64:
                case FieldType.Fixed64:
                    return ParseNumericString(keyText, ulong.Parse);
                default:
                    throw new InvalidTextProtocolBufferException("Invalid field type for map: " + field.FieldType);
            }
        }

        private static object ParseSingleNumberValue(FieldDescriptor field, TextToken token)
        {
            double value = token.NumberValue;
            checked
            {
                try
                {
                    switch (field.FieldType)
                    {
                        case FieldType.Int32:
                        case FieldType.SInt32:
                        case FieldType.SFixed32:
                            CheckInteger(value);
                            return (int) value;
                        case FieldType.UInt32:
                        case FieldType.Fixed32:
                            CheckInteger(value);
                            return (uint) value;
                        case FieldType.Int64:
                        case FieldType.SInt64:
                        case FieldType.SFixed64:
                            CheckInteger(value);
                            return (long) value;
                        case FieldType.UInt64:
                        case FieldType.Fixed64:
                            CheckInteger(value);
                            return (ulong) value;
                        case FieldType.Double:
                            return value;
                        case FieldType.Float:
                            if (double.IsNaN(value))
                            {
                                return float.NaN;
                            }
                            if (value > float.MaxValue || value < float.MinValue)
                            {
                                if (double.IsPositiveInfinity(value))
                                {
                                    return float.PositiveInfinity;
                                }
                                if (double.IsNegativeInfinity(value))
                                {
                                    return float.NegativeInfinity;
                                }
                                throw new InvalidTextProtocolBufferException($"Value out of range: {value}");
                            }
                            return (float) value;
                        case FieldType.Enum:
                            CheckInteger(value);
                            // Just return it as an int, and let the CLR convert it.
                            // Note that we deliberately don't check that it's a known value.
                            return (int) value;
                        default:
                            throw new InvalidTextProtocolBufferException($"Unsupported conversion from text number for field type {field.FieldType}");
                    }
                }
                catch (OverflowException)
                {
                    throw new InvalidTextProtocolBufferException($"Value out of range: {value}");
                }
            }
        }

        private static void CheckInteger(double value)
        {
            if (double.IsInfinity(value) || double.IsNaN(value))
            {
                throw new InvalidTextProtocolBufferException($"Value not an integer: {value}");
            }
            if (value != Math.Floor(value))
            {
                throw new InvalidTextProtocolBufferException($"Value not an integer: {value}");
            }
        }

        private static object ParseSingleStringValue(FieldDescriptor field, string text)
        {
            switch (field.FieldType)
            {
                case FieldType.String:
                    return text;
                case FieldType.Bytes:
                    try
                    {
                        return TextFormatter.DecodeBytes(text);
                    }
                    catch (FormatException e)
                    {
                        throw InvalidTextProtocolBufferException.InvalidBase64(e);
                    }
                case FieldType.Int32:
                case FieldType.SInt32:
                case FieldType.SFixed32:
                    return ParseNumericString(text, int.Parse);
                case FieldType.UInt32:
                case FieldType.Fixed32:
                    return ParseNumericString(text, uint.Parse);
                case FieldType.Int64:
                case FieldType.SInt64:
                case FieldType.SFixed64:
                    return ParseNumericString(text, long.Parse);
                case FieldType.UInt64:
                case FieldType.Fixed64:
                    return ParseNumericString(text, ulong.Parse);
                case FieldType.Double:
                    double d = ParseNumericString(text, double.Parse);
                    ValidateInfinityAndNan(text, double.IsPositiveInfinity(d), double.IsNegativeInfinity(d), double.IsNaN(d));
                    return d;
                case FieldType.Float:
                    float f = ParseNumericString(text, float.Parse);
                    ValidateInfinityAndNan(text, float.IsPositiveInfinity(f), float.IsNegativeInfinity(f), float.IsNaN(f));
                    return f;
                case FieldType.Enum:
                    var enumValue = field.EnumType.FindValueByName(text);

                    if (enumValue == null)
                    {
                        throw new InvalidTextProtocolBufferException($"Invalid enum value: {text} for enum type: {field.EnumType.FullName}");
                    }
                    // Just return it as an int, and let the CLR convert it.
                    return enumValue.Number;
                case FieldType.Bool:
                    if ("true".Equals(text, StringComparison.OrdinalIgnoreCase))
                        return true;
                    
                    if ("false".Equals(text, StringComparison.OrdinalIgnoreCase))
                        return false;

                    throw new InvalidTextProtocolBufferException($"Invalid bool value: {text}");

                default:
                    throw new InvalidTextProtocolBufferException($"Unsupported conversion from Text string for field type {field.FieldType}");
            }
        }

        /// <summary>
        /// Creates a new instance of the message type for the given field.
        /// </summary>
        private static IMessage NewMessageForField(FieldDescriptor field)
        {
            return field.MessageType.Parser.CreateTemplate();
        }

        private static T ParseNumericString<T>(string text, Func<string, NumberStyles, IFormatProvider, T> parser)
        {
            // Can't prohibit this with NumberStyles.
            if (text.StartsWith("+"))
            {
                throw new InvalidTextException($"Invalid numeric value: {text}");
            }
            if (text.StartsWith("0") && text.Length > 1)
            {
                if (text[1] >= '0' && text[1] <= '9')
                {
                    throw new InvalidTextException($"Invalid numeric value: {text}");
                }
            }
            else if (text.StartsWith("-0") && text.Length > 2)
            {
                if (text[2] >= '0' && text[2] <= '9')
                {
                    throw new InvalidTextException($"Invalid numeric value: {text}");
                }
            }
            try
            {
                return parser(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw new InvalidTextException($"Invalid numeric value for type: {text}");
            }
            catch (OverflowException)
            {
                throw new InvalidTextException($"Value out of range: {text}");
            }
        }

        /// <summary>
        /// Checks that any infinite/NaN values originated from the correct text.
        /// This corrects the lenient whitespace handling of double.Parse/float.Parse, as well as the
        /// way that Mono parses out-of-range values as infinity.
        /// </summary>
        private static void ValidateInfinityAndNan(string text, bool isPositiveInfinity, bool isNegativeInfinity, bool isNaN)
        {
            if ((isPositiveInfinity && text != "Infinity") ||
                (isNegativeInfinity && text != "-Infinity") ||
                (isNaN && text != "NaN"))
            {
                throw new InvalidTextException($"Invalid numeric value: {text}");
            }
        }

        private static void MergeTimestamp(IMessage message, TextToken token)
        {
            if (token.Type != TokenType.StringValue)
            {
                throw new InvalidTextProtocolBufferException("Expected string value for Timestamp");
            }
            var match = TimestampRegex.Match(token.StringValue);
            if (!match.Success)
            {
                throw new InvalidTextProtocolBufferException($"Invalid Timestamp value: {token.StringValue}");
            }
            var dateTime = match.Groups["datetime"].Value;
            var subseconds = match.Groups["subseconds"].Value;
            var offset = match.Groups["offset"].Value;

            try
            {
                DateTime parsed = DateTime.ParseExact(
                    dateTime,
                    "yyyy-MM-dd'T'HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                // TODO: It would be nice not to have to create all these objects... easy to optimize later though.
                Timestamp timestamp = Timestamp.FromDateTime(parsed);
                int nanosToAdd = 0;
                if (subseconds != "")
                {
                    // This should always work, as we've got 1-9 digits.
                    int parsedFraction = int.Parse(subseconds.Substring(1), CultureInfo.InvariantCulture);
                    nanosToAdd = parsedFraction * SubsecondScalingFactors[subseconds.Length];
                }
                int secondsToAdd = 0;
                if (offset != "Z")
                {
                    // This is the amount we need to *subtract* from the local time to get to UTC - hence - => +1 and vice versa.
                    int sign = offset[0] == '-' ? 1 : -1;
                    int hours = int.Parse(offset.Substring(1, 2), CultureInfo.InvariantCulture);
                    int minutes = int.Parse(offset.Substring(4, 2));
                    int totalMinutes = hours * 60 + minutes;
                    if (totalMinutes > 18 * 60)
                    {
                        throw new InvalidTextProtocolBufferException("Invalid Timestamp value: " + token.StringValue);
                    }
                    if (totalMinutes == 0 && sign == 1)
                    {
                        // This is an offset of -00:00, which means "unknown local offset". It makes no sense for a timestamp.
                        throw new InvalidTextProtocolBufferException("Invalid Timestamp value: " + token.StringValue);
                    }
                    // We need to *subtract* the offset from local time to get UTC.
                    secondsToAdd = sign * totalMinutes * 60;
                }
                // Ensure we've got the right signs. Currently unnecessary, but easy to do.
                if (secondsToAdd < 0 && nanosToAdd > 0)
                {
                    secondsToAdd++;
                    nanosToAdd = nanosToAdd - Duration.NanosecondsPerSecond;
                }
                if (secondsToAdd != 0 || nanosToAdd != 0)
                {
                    timestamp += new Duration { Nanos = nanosToAdd, Seconds = secondsToAdd };
                    // The resulting timestamp after offset change would be out of our expected range. Currently the Timestamp message doesn't validate this
                    // anywhere, but we shouldn't parse it.
                    if (timestamp.Seconds < ProtobufAdapter.UnixSecondsAtBclMinValue || timestamp.Seconds > ProtobufAdapter.UnixSecondsAtBclMaxValue)
                    {
                        throw new InvalidTextProtocolBufferException("Invalid Timestamp value: " + token.StringValue);
                    }
                }
                message.Descriptor.Fields[Timestamp.SecondsFieldNumber].Accessor.SetValue(message, timestamp.Seconds);
                message.Descriptor.Fields[Timestamp.NanosFieldNumber].Accessor.SetValue(message, timestamp.Nanos);
            }
            catch (FormatException)
            {
                throw new InvalidTextProtocolBufferException("Invalid Timestamp value: " + token.StringValue);
            }
        }

        private static void MergeDuration(IMessage message, TextToken token)
        {
            if (token.Type != TokenType.StringValue)
            {
                throw new InvalidTextProtocolBufferException("Expected string value for Duration");
            }
            var match = DurationRegex.Match(token.StringValue);
            if (!match.Success)
            {
                throw new InvalidTextProtocolBufferException("Invalid Duration value: " + token.StringValue);
            }
            var sign = match.Groups["sign"].Value;
            var secondsText = match.Groups["int"].Value;
            // Prohibit leading insignficant zeroes
            if (secondsText[0] == '0' && secondsText.Length > 1)
            {
                throw new InvalidTextProtocolBufferException("Invalid Duration value: " + token.StringValue);
            }
            var subseconds = match.Groups["subseconds"].Value;
            var multiplier = sign == "-" ? -1 : 1;

            try
            {
                long seconds = long.Parse(secondsText, CultureInfo.InvariantCulture) * multiplier;
                int nanos = 0;
                if (subseconds != "")
                {
                    // This should always work, as we've got 1-9 digits.
                    int parsedFraction = int.Parse(subseconds.Substring(1));
                    nanos = parsedFraction * SubsecondScalingFactors[subseconds.Length] * multiplier;
                }
                if (!ProtobufAdapter.IsNormalized(seconds, nanos))
                {
                    throw new InvalidTextProtocolBufferException($"Invalid Duration value: {token.StringValue}");
                }
                message.Descriptor.Fields[Duration.SecondsFieldNumber].Accessor.SetValue(message, seconds);
                message.Descriptor.Fields[Duration.NanosFieldNumber].Accessor.SetValue(message, nanos);
            }
            catch (FormatException)
            {
                throw new InvalidTextProtocolBufferException($"Invalid Duration value: {token.StringValue}");
            }
        }

        private static void MergeFieldMask(IMessage message, TextToken token)
        {
            if (token.Type != TokenType.StringValue)
            {
                throw new InvalidTextProtocolBufferException("Expected string value for FieldMask");
            }
            // TODO: Do we *want* to remove empty entries? Probably okay to treat "" as "no paths", but "foo,,bar"?
            string[] jsonPaths = token.StringValue.Split(FieldMaskPathSeparators, StringSplitOptions.RemoveEmptyEntries);
            IList messagePaths = (IList) message.Descriptor.Fields[FieldMask.PathsFieldNumber].Accessor.GetValue(message);
            foreach (var path in jsonPaths)
            {
                messagePaths.Add(ToSnakeCase(path));
            }
        }

        // Ported from src/google/protobuf/util/internal/utility.cc
        private static string ToSnakeCase(string text)
        {
            var builder = new StringBuilder(text.Length * 2);
            // Note: this is probably unnecessary now, but currently retained to be as close as possible to the
            // C++, whilst still throwing an exception on underscores.
            bool wasNotUnderscore = false;  // Initialize to false for case 1 (below)
            bool wasNotCap = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 'A' && c <= 'Z') // ascii_isupper
                {
                    // Consider when the current character B is capitalized:
                    // 1) At beginning of input:   "B..." => "b..."
                    //    (e.g. "Biscuit" => "biscuit")
                    // 2) Following a lowercase:   "...aB..." => "...a_b..."
                    //    (e.g. "gBike" => "g_bike")
                    // 3) At the end of input:     "...AB" => "...ab"
                    //    (e.g. "GoogleLAB" => "google_lab")
                    // 4) Followed by a lowercase: "...ABc..." => "...a_bc..."
                    //    (e.g. "GBike" => "g_bike")
                    if (wasNotUnderscore &&               //            case 1 out
                        (wasNotCap ||                     // case 2 in, case 3 out
                         (i + 1 < text.Length &&         //            case 3 out
                          (text[i + 1] >= 'a' && text[i + 1] <= 'z')))) // ascii_islower(text[i + 1])
                    {  // case 4 in
                       // We add an underscore for case 2 and case 4.
                        builder.Append('_');
                    }
                    // ascii_tolower, but we already know that c *is* an upper case ASCII character...
                    builder.Append((char) (c + 'a' - 'A'));
                    wasNotUnderscore = true;
                    wasNotCap = false;
                }
                else
                {
                    builder.Append(c);
                    if (c == '_')
                    {
                        throw new InvalidTextProtocolBufferException($"Invalid field mask: {text}");
                    }
                    wasNotUnderscore = true;
                    wasNotCap = true;
                }
            }
            return builder.ToString();
        }
        #endregion

        /// <summary>
        /// Settings controlling text parsing.
        /// </summary>
        public sealed class Settings
        {
            /// <summary>
            /// Default settings, as used by <see cref="TextParser.Default"/>. This has the same default
            /// recursion limit as <see cref="CodedInputStream"/>, and an empty type registry.
            /// </summary>
            public static Settings Default { get; }

            // Workaround for the Mono compiler complaining about XML comments not being on
            // valid language elements.
            static Settings()
            {
                Default = new Settings(ProtobufAdapter.DefaultRecursionLimit);
            }

            /// <summary>
            /// The maximum depth of messages to parse. Note that this limit only applies to parsing
            /// messages, not collections - so a message within a collection within a message only counts as
            /// depth 2, not 3.
            /// </summary>
            public int RecursionLimit { get; }

            /// <summary>
            /// The type registry used to parse <see cref="Any"/> messages.
            /// </summary>
            public TypeRegistry TypeRegistry { get; }

            /// <summary>
            /// Whether the parser should ignore unknown fields (<c>true</c>) or throw an exception when
            /// they are encountered (<c>false</c>).
            /// </summary>
            public bool IgnoreUnknownFields { get; }

            private Settings(int recursionLimit, TypeRegistry typeRegistry, bool ignoreUnknownFields)
            {
                RecursionLimit = recursionLimit;
                TypeRegistry = ProtoPreconditions.CheckNotNull(typeRegistry, nameof(typeRegistry));
                IgnoreUnknownFields = ignoreUnknownFields;
            }

            /// <summary>
            /// Creates a new <see cref="Settings"/> object with the specified recursion limit.
            /// </summary>
            /// <param name="recursionLimit">The maximum depth of messages to parse</param>
            public Settings(int recursionLimit) : this(recursionLimit, TypeRegistry.Empty)
            {
            }

            /// <summary>
            /// Creates a new <see cref="Settings"/> object with the specified recursion limit and type registry.
            /// </summary>
            /// <param name="recursionLimit">The maximum depth of messages to parse</param>
            /// <param name="typeRegistry">The type registry used to parse <see cref="Any"/> messages</param>
            public Settings(int recursionLimit, TypeRegistry typeRegistry) : this(recursionLimit, typeRegistry, false)
            {
            }

            /// <summary>
            /// Creates a new <see cref="Settings"/> object set to either ignore unknown fields, or throw an exception
            /// when unknown fields are encountered.
            /// </summary>
            /// <param name="ignoreUnknownFields"><c>true</c> if unknown fields should be ignored when parsing; <c>false</c> to throw an exception.</param>
            public Settings WithIgnoreUnknownFields(bool ignoreUnknownFields) =>
                new Settings(RecursionLimit, TypeRegistry, ignoreUnknownFields);

            /// <summary>
            /// Creates a new <see cref="Settings"/> object based on this one, but with the specified recursion limit.
            /// </summary>
            /// <param name="recursionLimit">The new recursion limit.</param>
            public Settings WithRecursionLimit(int recursionLimit) =>
                new Settings(recursionLimit, TypeRegistry, IgnoreUnknownFields);

            /// <summary>
            /// Creates a new <see cref="Settings"/> object based on this one, but with the specified type registry.
            /// </summary>
            /// <param name="typeRegistry">The new type registry. Must not be null.</param>
            public Settings WithTypeRegistry(TypeRegistry typeRegistry) =>
                new Settings(
                    RecursionLimit,
                    ProtoPreconditions.CheckNotNull(typeRegistry, nameof(typeRegistry)),
                    IgnoreUnknownFields);
        }
    }
}