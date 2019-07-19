
namespace StreamJsonRpc
{
    using System;
    using System.Linq;
    using System.Reflection;
    using Microsoft;

    /// <summary>
    /// Class containing useful method to help on the implementation of message formatters.
    /// </summary>
    public class MessageFormatterHelper
    {
        /// <summary>
        /// Converts given <see cref="Type"/> to its <see cref="IProgress{T}"/> type.
        /// </summary>
        /// <param name="objectType">The type which may implement <see cref="IProgress{T}"/>.</param>
        /// <returns>The <see cref="IProgress{T}"/> from given <see cref="Type"/> object, or <c>null</c>  if no such interface was found in the given <paramref name="objectType" />.</returns>
        public static Type FindIProgressOfT(Type objectType)
        {
            Requires.NotNull(objectType, nameof(objectType));

            if (objectType.IsConstructedGenericType && objectType.GetGenericTypeDefinition().Equals(typeof(IProgress<>)))
            {
                return objectType;
            }

            return objectType.GetTypeInfo().GetInterfaces().FirstOrDefault(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == typeof(IProgress<>));
        }

        internal class ProgressParamInformation
        {
            public ProgressParamInformation(object progressObject)
            {
                Requires.NotNull(progressObject, nameof(progressObject));

                Type iProgressOfTType = MessageFormatterHelper.FindIProgressOfT(progressObject.GetType());

                Verify.Operation(iProgressOfTType != null, Resources.FindIProgressOfTError);

                this.ValueType = iProgressOfTType.GenericTypeArguments[0];
                this.ReportMethod = iProgressOfTType.GetRuntimeMethod(nameof(IProgress<int>.Report), new Type[] { this.ValueType });
                this.ProgressObject = progressObject;
            }

            internal Type ValueType { get; }

            internal MethodInfo ReportMethod { get; }

            internal object ProgressObject { get; }
        }
    }
}
