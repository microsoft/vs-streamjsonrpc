// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using Microsoft;

    /// <summary>
    /// A collection of extension methods to support special awaiters.
    /// </summary>
    internal static class AwaitExtensions
    {
        /// <summary>
        /// Gets an awaiter that switches the caller to execute on the specified <see cref="SynchronizationContext"/>.
        /// </summary>
        /// <param name="synchronizationContext">The <see cref="SynchronizationContext"/> to switch to.</param>
        /// <returns>The value to await on.</returns>
        internal static SynchronizationContextAwaiter GetAwaiter(this SynchronizationContext synchronizationContext)
        {
            Requires.NotNull(synchronizationContext, nameof(synchronizationContext));
            return new SynchronizationContextAwaiter(synchronizationContext);
        }

        /// <summary>
        /// The awaiter for <see cref="SynchronizationContext"/>.
        /// </summary>
        internal struct SynchronizationContextAwaiter : INotifyCompletion
        {
            /// <summary>
            /// The <see cref="SynchronizationContext"/> to switch the caller's context to.
            /// </summary>
            private readonly SynchronizationContext synchronizationContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="SynchronizationContextAwaiter"/> struct.
            /// </summary>
            /// <param name="synchronizationContext">The <see cref="SynchronizationContext"/> to switch the caller's context to.</param>
            internal SynchronizationContextAwaiter(SynchronizationContext synchronizationContext)
            {
                Requires.NotNull(synchronizationContext, nameof(synchronizationContext));
                this.synchronizationContext = synchronizationContext;
            }

            /// <summary>
            /// Gets a value indicating whether the caller is already on the desired context.
            /// </summary>
            /// <remarks>
            /// We always return <c>false</c> because we use this to invoke server methods and we *always* want to
            /// yield before invoking them, even if this is the default SynchronizationContext that the caller is on.
            /// </remarks>
            public bool IsCompleted => false;

            /// <summary>
            /// Does nothing.
            /// </summary>
            public void GetResult()
            {
            }

            /// <summary>
            /// Schedules a continuation on the <see cref="SynchronizationContext"/> specified in the constructor.
            /// </summary>
            /// <param name="continuation">The delegate to execute on the <see cref="SynchronizationContext"/>.</param>
            public void OnCompleted(Action continuation)
            {
#pragma warning disable VSTHRD001 // Avoid legacy threading switching APIs
                this.synchronizationContext.Post(action => ((Action)action!).Invoke(), continuation);
#pragma warning restore VSTHRD001 // Avoid legacy threading switching APIs
            }
        }
    }
}
