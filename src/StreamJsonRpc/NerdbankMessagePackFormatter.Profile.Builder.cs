﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.IO.Pipelines;
using Nerdbank.MessagePack;
using PolyType;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    /// <summary>
    /// A serialization profile for the <see cref="NerdbankMessagePackFormatter"/>.
    /// </summary>
    public partial class Profile
    {
        /// <summary>
        /// Provides methods to build a serialization profile for the <see cref="NerdbankMessagePackFormatter"/>.
        /// </summary>
        public class Builder
        {
            private readonly Profile baseProfile;

            private ImmutableArray<ITypeShapeProvider>.Builder? typeShapeProvidersBuilder = null;

            /// <summary>
            /// Initializes a new instance of the <see cref="Builder"/> class.
            /// </summary>
            /// <param name="baseProfile">The base profile to build upon.</param>
            internal Builder(Profile baseProfile)
            {
                this.baseProfile = baseProfile;
            }

            /// <summary>
            /// Adds a type shape provider to the context.
            /// </summary>
            /// <param name="provider">The type shape provider to add.</param>
            public void AddTypeShapeProvider(ITypeShapeProvider provider)
            {
                this.typeShapeProvidersBuilder ??= ImmutableArray.CreateBuilder<ITypeShapeProvider>();
                this.typeShapeProvidersBuilder.Add(provider);
            }

            /// <summary>
            /// Registers an async enumerable type with the context.
            /// </summary>
            /// <typeparam name="TEnumerable">The type of the async enumerable.</typeparam>
            /// <typeparam name="TElement">The type of the elements in the async enumerable.</typeparam>
            public void RegisterAsyncEnumerableType<TEnumerable, TElement>()
                where TEnumerable : IAsyncEnumerable<TElement>
            {
                MessagePackConverter<TEnumerable> converter = AsyncEnumerableConverterResolver.GetConverter<TEnumerable>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a converter with the context.
            /// </summary>
            /// <typeparam name="T">The type the converter handles.</typeparam>
            /// <param name="converter">The converter to register.</param>
            public void RegisterConverter<T>(MessagePackConverter<T> converter)
            {
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers known subtypes for a base type with the context.
            /// </summary>
            /// <typeparam name="TBase">The base type.</typeparam>
            /// <param name="mapping">The mapping of known subtypes.</param>
            public void RegisterKnownSubTypes<TBase>(KnownSubTypeMapping<TBase> mapping)
            {
                this.baseProfile.Serializer.RegisterKnownSubTypes(mapping);
            }

            /// <summary>
            /// Registers a progress type with the context.
            /// </summary>
            /// <typeparam name="TProgress">The type of the progress.</typeparam>
            /// <typeparam name="TReport">The type of the report.</typeparam>
            public void RegisterProgressType<TProgress, TReport>()
                where TProgress : IProgress<TReport>
            {
                MessagePackConverter<TProgress> converter = ProgressConverterResolver.GetConverter<TProgress>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a duplex pipe type with the context.
            /// </summary>
            /// <typeparam name="TPipe">The type of the duplex pipe.</typeparam>
            public void RegisterDuplexPipeType<TPipe>()
                where TPipe : IDuplexPipe
            {
                MessagePackConverter<TPipe> converter = PipeConverterResolver.GetConverter<TPipe>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a pipe reader type with the context.
            /// </summary>
            /// <typeparam name="TReader">The type of the pipe reader.</typeparam>
            public void RegisterPipeReaderType<TReader>()
                where TReader : PipeReader
            {
                MessagePackConverter<TReader> converter = PipeConverterResolver.GetConverter<TReader>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a pipe writer type with the context.
            /// </summary>
            /// <typeparam name="TWriter">The type of the pipe writer.</typeparam>
            public void RegisterPipeWriterType<TWriter>()
                where TWriter : PipeWriter
            {
                MessagePackConverter<TWriter> converter = PipeConverterResolver.GetConverter<TWriter>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a stream type with the context.
            /// </summary>
            /// <typeparam name="TStream">The type of the stream.</typeparam>
            public void RegisterStreamType<TStream>()
                where TStream : Stream
            {
                MessagePackConverter<TStream> converter = PipeConverterResolver.GetConverter<TStream>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers an exception type with the context.
            /// </summary>
            /// <typeparam name="TException">The type of the exception.</typeparam>
            public void RegisterExceptionType<TException>()
                where TException : Exception
            {
                MessagePackConverter<TException> converter = MessagePackExceptionConverterResolver.GetConverter<TException>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers an RPC marshalable type with the context.
            /// </summary>
            /// <typeparam name="T">The type to register.</typeparam>
            public void RegisterRpcMarshalableType<T>()
                where T : class
            {
                if (MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(
                    typeof(T),
                    out JsonRpcProxyOptions? proxyOptions,
                    out JsonRpcTargetOptions? targetOptions,
                    out RpcMarshalableAttribute? attribute))
                {
                    var converter = (RpcMarshalableConverter<T>)Activator.CreateInstance(
                        typeof(RpcMarshalableConverter<>).MakeGenericType(typeof(T)),
                        proxyOptions,
                        targetOptions,
                        attribute)!;

                    this.baseProfile.Serializer.RegisterConverter(converter);
                    return;
                }

                // TODO: Throw?
                throw new NotSupportedException();
            }

            /// <summary>
            /// Builds the formatter profile.
            /// </summary>
            /// <returns>The built formatter profile.</returns>
            public Profile Build()
            {
                if (this.typeShapeProvidersBuilder is null || this.typeShapeProvidersBuilder.Count < 1)
                {
                    return this.baseProfile;
                }

                return new Profile(
                    this.baseProfile.Source,
                    this.baseProfile.Serializer,
                    this.typeShapeProvidersBuilder.ToImmutable());
            }
        }
    }
}
