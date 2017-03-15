using System;

namespace StreamJsonRpc
{
    /// <summary>
    /// Attribute defined on a class which indicates that the parameter should be passed as an object, instead of array of one object.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class JsonRpcParameterObjectAttribute : Attribute
    {
    }
}
