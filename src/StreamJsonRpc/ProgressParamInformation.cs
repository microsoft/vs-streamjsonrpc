

namespace StreamJsonRpc
{
    using System;
    using System.Reflection;
    using Microsoft;

    public class ProgressParamInformation
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

        public Type ValueType { get; }

        public MethodInfo ReportMethod { get; }

        public object ProgressObject { get; }
    }
}
