
namespace StreamJsonRpc
{
    using System;
    using System.Linq;
    using System.Reflection;

    public class MessageFormatterHelper
    {
        public static Type FindIProgressOfT(Type objectType)
        {
            if (objectType.IsConstructedGenericType && objectType.GetGenericTypeDefinition().Equals(typeof(IProgress<>)))
            {
                return objectType;
            }
            else if (objectType.GetTypeInfo().GetInterfaces().Any(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == typeof(IProgress<>)))
            {
                return typeof(IProgress<>).MakeGenericType(objectType.GenericTypeArguments);
            }

            return null;
        }
    }
}
