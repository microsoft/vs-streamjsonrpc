// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Nerdbank.MessagePack;
using PolyType;
using PolyType.Abstractions;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public sealed partial class NerdbankMessagePackFormatter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Profile"/> class.
    /// </summary>
    /// <param name="serializer">The MessagePack serializer to use.</param>
    /// <param name="shapeProviders">The type shape providers to use.</param>
    [DebuggerDisplay($"{{{nameof(GetDebuggerDisplay)}(),nq}}")]
    public partial class Profile(MessagePackSerializer serializer, ImmutableArray<ITypeShapeProvider> shapeProviders)
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Profile"/> class.
        /// </summary>
        /// <param name="source">The source of the profile.</param>
        /// <param name="serializer">The MessagePack serializer to use.</param>
        /// <param name="shapeProviders">The type shape providers to use.</param>
        internal Profile(ProfileSource source, MessagePackSerializer serializer, ImmutableArray<ITypeShapeProvider> shapeProviders)
            : this(serializer, shapeProviders)
        {
            this.Source = source;
        }

        internal enum ProfileSource
        {
            /// <summary>
            /// The profile is internal to the formatter.
            /// </summary>
            Internal,

            /// <summary>
            /// The profile is external to the formatter.
            /// </summary>
            External,
        }

        internal ProfileSource Source { get; } = ProfileSource.Internal;

        /// <summary>
        /// Gets the MessagePack serializer.
        /// </summary>
        internal MessagePackSerializer Serializer => serializer;

        /// <summary>
        /// Gets the shape provider resolver.
        /// </summary>
        internal TypeShapeProviderResolver ShapeProviderResolver { get; } = new TypeShapeProviderResolver(shapeProviders);

        private int ProvidersCount => shapeProviders.Length;

        internal Profile WithFormatterState(NerdbankMessagePackFormatter formatter)
        {
            SerializationContext nextContext = serializer.StartingContext;
            nextContext[SerializationContextExtensions.FormatterKey] = formatter;
            MessagePackSerializer nextSerializer = serializer with { StartingContext = nextContext };

            return new(this.Source, nextSerializer, shapeProviders);
        }

        private string GetDebuggerDisplay() => $"{this.Source} [{this.ProvidersCount}]";

        /// <summary>
        /// When passing a type shape provider to MessagePackSerializer, it will resolve the type shape
        /// from that provider and try to cache it. If the resolved type shape is not sourced from the
        /// passed provider, it will throw an ArgumentException with the message:
        /// System.ArgumentException : The specified shape provider is not valid for this cache.
        /// To avoid this, this class does not implement ITypeShapeProvider directly so that it cannot
        /// be passed to the serializer. Instead, use <see cref="ResolveShapeProvider{T}()"/> to get the
        /// provider that will resolve the shape for the specified type, or if the serialization method supports
        /// if use <see cref="ResolveShape{T}()"/> to get the shape directly.
        /// Related issue: https://github.com/eiriktsarpalis/PolyType/issues/92.
        /// </summary>
        internal class TypeShapeProviderResolver
        {
            private readonly ImmutableArray<ITypeShapeProvider> providers;

            internal TypeShapeProviderResolver(ImmutableArray<ITypeShapeProvider> providers)
            {
                this.providers = providers;
            }

            public ITypeShape<T> ResolveShape<T>() => (ITypeShape<T>)this.ResolveShape(typeof(T));

            public ITypeShape ResolveShape(Type type)
            {
                foreach (ITypeShapeProvider provider in this.providers)
                {
                    ITypeShape? shape = provider.GetShape(type);
                    if (shape is not null)
                    {
                        return shape;
                    }
                }

                // TODO: Loc the exception message.
                throw new MessagePackSerializationException($"No shape provider found for type '{type}'.");
            }

            /// <summary>
            /// Find the first provider that can provide a shape for the specified type.
            /// </summary>
            /// <typeparam name="T">The type to resolve.</typeparam>
            /// <returns>The relevant shape provider.</returns>
            public ITypeShapeProvider ResolveShapeProvider<T>() => this.ResolveShapeProvider(typeof(T));

            /// <inheritdoc cref="ResolveShapeProvider{T}()"/>
            public ITypeShapeProvider ResolveShapeProvider(Type type)
            {
                foreach (ITypeShapeProvider provider in this.providers)
                {
                    if (provider.GetShape(type) is not null)
                    {
                        return provider;
                    }
                }

                // TODO: Loc the exception message.
                throw new MessagePackSerializationException($"No shape provider found for type '{type}'.");
            }
        }
    }
}
