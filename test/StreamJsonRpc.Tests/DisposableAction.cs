// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using Microsoft;

internal class DisposableAction : IDisposableObservable
{
    private readonly Action? disposeAction;

    internal DisposableAction(Action? disposeAction)
    {
        this.disposeAction = disposeAction;
    }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        this.IsDisposed = true;
        this.disposeAction?.Invoke();
    }
}
