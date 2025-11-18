#if NOTDOCFX // Workaround https://github.com/dotnet/docfx/issues/10808

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using PolyType.Abstractions;

namespace StreamJsonRpc;

/// <summary>
/// Extension methods for the <see cref="RpcTargetMetadata"/> class.
/// </summary>
public static class RpcTargetMetadataExtensions
{
    extension(RpcTargetMetadata)
    {
        /// <summary>
        /// Creates an <see cref="RpcTargetMetadata"/> instance from the specified shape.
        /// </summary>
        /// <typeparam name="T">The type for which a shape should be obtained and <see cref="RpcTargetMetadata"/> generated for.</typeparam>
        /// <returns>An <see cref="RpcTargetMetadata"/> instance initialized from the shape of the <typeparamref name="T"/>.</returns>
#if NET8_0
        [RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use the RpcTargetMetadata.FromShape<T>() method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
        public static RpcTargetMetadata FromShape<T>()
            => RpcTargetMetadata.FromShape(TypeShapeResolver.ResolveDynamicOrThrow<T>());

        /// <summary>
        /// Creates an <see cref="RpcTargetMetadata"/> instance from the specified shape.
        /// </summary>
        /// <typeparam name="T">The type for which a shape should be obtained and <see cref="RpcTargetMetadata"/> generated for.</typeparam>
        /// <typeparam name="TProvider">The provider of type shapes from which to obtain the shape.</typeparam>
        /// <returns>An <see cref="RpcTargetMetadata"/> instance initialized from the shape of the <typeparamref name="T"/>.</returns>
#if NET8_0
        [RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use the RpcTargetMetadata.FromShape<T, TProvider>() method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
        public static RpcTargetMetadata FromShape<T, TProvider>()
            => RpcTargetMetadata.FromShape(TypeShapeResolver.ResolveDynamicOrThrow<T, TProvider>());
    }

#if NET8_0
    /// <summary>
    /// A message to use as the argument to <see cref="RequiresDynamicCodeAttribute"/>
    /// for methods that call into <see cref="TypeShapeResolver.ResolveDynamicOrThrow{T}"/>.
    /// </summary>
    /// <seealso href="https://github.com/dotnet/runtime/issues/119440#issuecomment-3269894751"/>
    private const string ResolveDynamicMessage =
        "Dynamic resolution of IShapeable<T> interface may require dynamic code generation in .NET 8 Native AOT. " +
        "It is recommended to switch to statically resolved IShapeable<T> APIs or upgrade your app to .NET 9 or later.";
#endif
}

#endif
