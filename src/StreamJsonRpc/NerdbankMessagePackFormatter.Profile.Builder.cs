// Copyright (c) Microsoft Corporation. All rights reserved.
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
            /// Adds a type shape provider to the profile.
            /// </summary>
            /// <param name="provider">The type shape provider to add.</param>
            public void AddTypeShapeProvider(ITypeShapeProvider provider)
            {
                this.typeShapeProvidersBuilder ??= ImmutableArray.CreateBuilder<ITypeShapeProvider>(initialCapacity: 3);
                this.typeShapeProvidersBuilder.Add(provider);
            }

            /// <summary>
            /// Registers known subtypes for a base type with the profile.
            /// </summary>
            /// <typeparam name="TBase">The base type.</typeparam>
            /// <param name="mapping">The mapping of known subtypes.</param>
            public void RegisterKnownSubTypes<TBase>(KnownSubTypeMapping<TBase> mapping)
            {
                this.baseProfile.Serializer.RegisterKnownSubTypes(mapping);
            }

            /// <summary>
            /// Registers a converter with the profile.
            /// </summary>
            /// <typeparam name="T">The type the converter handles.</typeparam>
            /// <param name="converter">The converter to register.</param>
            public void RegisterConverter<T>(MessagePackConverter<T> converter)
            {
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers an async enumerable converter with the profile.
            /// </summary>
            /// <remarks>
            /// Register an <see cref="IReadOnlyList{T}"/> on the type shape provider to avoid reflection costs.
            /// </remarks>
            /// <typeparam name="TElement">The type of the elements in the async enumerable.</typeparam>
            public void RegisterAsyncEnumerableConverter<TElement>()
            {
                MessagePackConverter<IAsyncEnumerable<TElement>> converter = new AsyncEnumerableConverters.PreciseTypeConverter<TElement>();
                this.baseProfile.Serializer.RegisterConverter(converter);

                MessagePackConverter<MessageFormatterEnumerableTracker.EnumeratorResults<TElement>> resultConverter = new EnumeratorResultsConverter<TElement>();
                this.baseProfile.Serializer.RegisterConverter(resultConverter);
            }

            /// <inheritdoc cref="RegisterAsyncEnumerableConverter{TElement}()"/>
            /// <typeparam name="TGenerator">The type of the async enumerable generator.</typeparam>
            /// <typeparam name="TElement">The type of the elements in the async enumerable.</typeparam>
            public void RegisterAsyncEnumerableConverter<TGenerator, TElement>()
                where TGenerator : IAsyncEnumerable<TElement>
            {
                MessagePackConverter<TGenerator> converter = new AsyncEnumerableConverters.GeneratorConverter<TGenerator, TElement>();
                this.baseProfile.Serializer.RegisterConverter(converter);
                MessagePackConverter<MessageFormatterEnumerableTracker.EnumeratorResults<TElement>> resultConverter = new EnumeratorResultsConverter<TElement>();
                this.baseProfile.Serializer.RegisterConverter(resultConverter);
            }

            /// <summary>
            /// Registers a progress type with the profile.
            /// </summary>
            /// <typeparam name="TReport">The type of the report.</typeparam>
            public void RegisterProgressConverter<TReport>()
            {
                MessagePackConverter<IProgress<TReport>> converter = ProgressConverterResolver.GetConverter<IProgress<TReport>>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <inheritdoc cref="RegisterProgressConverter{TReport}()" />
            /// <typeparam name="TProgress">The type of the progress.</typeparam>
            /// <typeparam name="TReport">The type of the report.</typeparam>
            public void RegisterProgressConverter<TProgress, TReport>()
                where TProgress : IProgress<TReport>
            {
                MessagePackConverter<TProgress> converter = ProgressConverterResolver.GetConverter<TProgress>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers an exception type with the profile.
            /// </summary>
            /// <typeparam name="TException">The type of the exception.</typeparam>
            public void RegisterExceptionType<TException>()
                where TException : Exception
            {
                MessagePackConverter<TException> converter = ExceptionConverter<TException>.Instance;
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers an RPC marshalable type with the profile.
            /// </summary>
            /// <typeparam name="T">The type to register.</typeparam>
            public void RegisterRpcMarshalableConverter<T>()
                where T : class
            {
                MessagePackConverter<T> converter = GetRpcMarshalableConverter<T>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a converter for the <see cref="IDuplexPipe"/> type with the profile.
            /// </summary>
            /// <typeparam name="T">The type of the duplex pipe.</typeparam>
            public void RegisterDuplexPipeConverter<T>()
                where T : class, IDuplexPipe
            {
                var converter = new PipeConverters.DuplexPipeConverter<T>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a converter for the <see cref="PipeReader"/> type with the profile.
            /// </summary>
            /// <typeparam name="T">The type of the pipe reader.</typeparam>
            public void RegisterPipeReaderConverter<T>()
                where T : PipeReader
            {
                var converter = new PipeConverters.PipeReaderConverter<T>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a converter for the <see cref="PipeWriter"/> type with the profile.
            /// </summary>
            /// <typeparam name="T">The type of the pipe writer.</typeparam>
            public void RegisterPipeWriterConverter<T>()
                where T : PipeWriter
            {
                var converter = new PipeConverters.PipeWriterConverter<T>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a converter for the <see cref="Stream"/> type with the profile.
            /// </summary>
            /// <typeparam name="T">The type of the stream.</typeparam>
            public void RegisterStreamConverter<T>()
                where T : Stream
            {
                var converter = new PipeConverters.StreamConverter<T>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers an observer type with the profile.
            /// </summary>
            /// <typeparam name="T">The type of the observer.</typeparam>
            public void RegisterObserverConverter<T>()
            {
                MessagePackConverter<IObserver<T>> converter = GetRpcMarshalableConverter<IObserver<T>>();
                this.baseProfile.Serializer.RegisterConverter(converter);
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

                // ExoticTypeShapeProvider is always first and cannot be overridden.
                return new Profile(
                    this.baseProfile.Source,
                    this.baseProfile.Serializer,
                    [ExoticTypeShapeProvider.Instance, .. this.typeShapeProvidersBuilder]);
            }
        }
    }
}
