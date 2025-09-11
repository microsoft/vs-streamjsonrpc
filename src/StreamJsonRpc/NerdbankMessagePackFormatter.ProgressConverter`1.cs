// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using PolyType;
using PolyType.Abstractions;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    /// <summary>
    /// Converts a progress token to an <see cref="IProgress{T}"/> or an <see cref="IProgress{T}"/> into a token.
    /// </summary>
    /// <typeparam name="TClass">The closed <see cref="IProgress{T}"/> interface.</typeparam>
    private class ProgressConverter<TClass> : MessagePackConverter<TClass>
    {
        private Func<JsonRpc, object, bool, TClass>? progressProxyCtor;

        [return: MaybeNull]
        public override TClass? Read(ref MessagePackReader reader, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();

            context.DepthStep();

            if (reader.TryReadNil())
            {
                return default;
            }

            Assumes.NotNull(formatter.JsonRpc);
            RawMessagePack token = reader.ReadRaw(context).ToOwned();
            bool clientRequiresNamedArgs = formatter.ApplicableMethodAttributeOnDeserializingMethod?.ClientRequiresNamedArguments is true;

            if (this.progressProxyCtor is null)
            {
                ITypeShape typeShape = context.TypeShapeProvider?.GetTypeShapeOrThrow(typeof(TClass)) ?? throw new InvalidOperationException("No TypeShapeProvider available.");
                IObjectTypeShape progressProxyShape = (IObjectTypeShape?)typeShape.GetAssociatedTypeShape(typeof(MessageFormatterProgressTracker.ProgressProxy<>)) ?? throw new InvalidOperationException("Unable to get ProgressProxy associated shape.");
                this.progressProxyCtor = (Func<JsonRpc, object, bool, TClass>?)progressProxyShape.Constructor?.Accept(NonDefaultConstructorVisitor<JsonRpc, object, bool>.Instance) ?? throw new InvalidOperationException("Unable to construct IProgress<T> proxy.");
            }

            return this.progressProxyCtor(formatter.JsonRpc, token, clientRequiresNamedArgs);
        }

        public override void Write(ref MessagePackWriter writer, in TClass? value, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();

            context.DepthStep();

            if (value is null)
            {
                writer.WriteNil();
            }
            else
            {
                long progressId = formatter.FormatterProgressTracker.GetTokenForProgress(value);
                writer.Write(progressId);
            }
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
    }
}
