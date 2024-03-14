// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

internal class DisposableAction : IDisposableObservable
{
    private static readonly Action EmptyAction = () => { };
    private Action? disposeAction;

    internal DisposableAction(Action? disposeAction)
    {
        this.disposeAction = disposeAction ?? EmptyAction;
    }

    public bool IsDisposed => this.disposeAction is null;

    public void Dispose()
    {
        Interlocked.Exchange(ref this.disposeAction, null)?.Invoke();
    }
}
