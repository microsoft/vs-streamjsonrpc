﻿// <auto-generated/>

#nullable enable

namespace StreamJsonRpc.Proxies;

internal class IFoo_Proxy : global::IFoo, global::StreamJsonRpc.IJsonRpcClientProxyInternal
{
	private readonly global::StreamJsonRpc.JsonRpc client;
	private readonly global::StreamJsonRpc.JsonRpcProxyOptions options;
	private readonly global::System.Action? onDispose;
	private readonly long? marshaledObjectHandle;
	
	private global::System.EventHandler<string>? callingMethod;
	private global::System.EventHandler<string>? calledMethod;
	private bool disposed;
	public IFoo_Proxy(global::StreamJsonRpc.JsonRpc client, global::StreamJsonRpc.JsonRpcProxyOptions options, long? marshaledObjectHandle, global::System.Action? onDispose)
	{
	    this.client = client ?? throw new global::System.ArgumentNullException(nameof(client));
	    this.options = options ?? throw new global::System.ArgumentNullException(nameof(options));
	    this.marshaledObjectHandle = marshaledObjectHandle;
	    this.onDispose = onDispose;
	}
	
	event global::System.EventHandler<string> global::StreamJsonRpc.IJsonRpcClientProxyInternal.CallingMethod
	{
	    add => this.callingMethod += value;
	    remove => this.callingMethod -= value;
	}
	
	event global::System.EventHandler<string> global::StreamJsonRpc.IJsonRpcClientProxyInternal.CalledMethod
	{
	    add => this.calledMethod += value;
	    remove => this.calledMethod -= value;
	}
	
	global::StreamJsonRpc.JsonRpc global::StreamJsonRpc.IJsonRpcClientProxy.JsonRpc => this.client;
	
	bool global::Microsoft.IDisposableObservable.IsDisposed => this.disposed;
	
	long? global::StreamJsonRpc.IJsonRpcClientProxyInternal.MarshaledObjectHandle => this.marshaledObjectHandle;
	
	public void Dispose()
	{
	    if (this.disposed)
	    {
	        return;
	    }
	    this.disposed = true;
	
	    if (this.onDispose is not null)
	    {
	        this.onDispose();
	    }
	    else
	    {
	        client.Dispose();
	    }
	}
}
