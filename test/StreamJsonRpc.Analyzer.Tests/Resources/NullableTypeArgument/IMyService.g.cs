﻿// <auto-generated/>

#nullable enable
#pragma warning disable CS0436 // prefer local types to imported ones

[global::StreamJsonRpc.Reflection.JsonRpcProxyMappingAttribute(typeof(StreamJsonRpc.Generated.IMyService_Proxy))]
partial interface IMyService
{
}

namespace StreamJsonRpc.Generated
{
	
	[global::System.CodeDom.Compiler.GeneratedCodeAttribute("StreamJsonRpc.Analyzers", "x.x.x.x")]
	internal class IMyService_Proxy : global::StreamJsonRpc.Reflection.ProxyBase
		, global::IMyService
	{
		
		private static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Type> GetNullableIntAsyncNamedArgumentDeclaredTypes1 = new global::System.Collections.Generic.Dictionary<string, global::System.Type>
		{
			["value"] = typeof(string),
		};
		
		private static readonly global::System.Collections.Generic.IReadOnlyList<global::System.Type> GetNullableIntAsyncPositionalArgumentDeclaredTypes1 = new global::System.Collections.Generic.List<global::System.Type>
		{
			typeof(string),
		};
		
		private string? transformedGetNullableIntAsync1;
		
		public IMyService_Proxy(global::StreamJsonRpc.JsonRpc client, global::StreamJsonRpc.Reflection.ProxyInputs inputs)
		    : base(client, inputs)
		{
		}
		
		global::System.Threading.Tasks.Task<object?> global::IMyService.GetNullableIntAsync(string? value)
		{
			if (this.IsDisposed) throw new global::System.ObjectDisposedException(this.GetType().FullName);
			
			this.OnCallingMethod("GetNullableIntAsync");
			string rpcMethodName = this.transformedGetNullableIntAsync1 ??= this.Options.MethodNameTransform("GetNullableIntAsync");
			global::System.Threading.Tasks.Task<object?> result = this.Options.ServerRequiresNamedArguments ?
			    this.JsonRpc.InvokeWithParameterObjectAsync<object?>(rpcMethodName, ConstructNamedArgs(), GetNullableIntAsyncNamedArgumentDeclaredTypes1, default) :
			    this.JsonRpc.InvokeWithCancellationAsync<object?>(rpcMethodName, [value], GetNullableIntAsyncPositionalArgumentDeclaredTypes1, default);
			this.OnCalledMethod("GetNullableIntAsync");
			
			return result;
			
			global::System.Collections.Generic.Dictionary<string, object?> ConstructNamedArgs()
			    => new()
			    {
					["value"] = value,
				};
		}
	}
}
