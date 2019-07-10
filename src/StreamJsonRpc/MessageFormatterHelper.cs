
namespace StreamJsonRpc
{
    using System;
    using System.Linq;
    using System.Reflection;

    /// <summary>
    /// Class containing useful method to help on the implementation of message formatters.
    /// </summary>
    public class MessageFormatterHelper
    {
        /// <summary>
        /// Converts given <see cref="Type"/> to its IProgress <see cref="Type"/>.
        /// </summary>
        /// <param name="objectType">The type implementing IProgress. </param>
        /// <returns>The IProgress <see cref="Type"/> from given <see cref="Type"/> object.</returns>
        public static Type FindIProgressOfT(Type objectType)
        {
            if (objectType != null)
            {
                if (objectType.IsConstructedGenericType && objectType.GetGenericTypeDefinition().Equals(typeof(IProgress<>)))
                {
                    return objectType;
                }
            }

            return objectType.GetTypeInfo().GetInterfaces().FirstOrDefault(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == typeof(IProgress<>));
        }
    }
}
