#if !NETCOREAPP3_0_OR_GREATER && !NETSTANDARD2_1_OR_GREATER
#pragma warning disable IDE0130
namespace System.Diagnostics.CodeAnalysis {
#pragma warning restore IDE0130
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class MaybeNullWhenAttribute(bool returnValue) : Attribute {
        public bool ReturnValue { get; } = returnValue;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class NotNullAttribute : Attribute {
    }
}
#endif

#if !NET6_0_OR_GREATER
#pragma warning disable IDE0130
namespace System.Runtime.CompilerServices {
#pragma warning restore IDE0130
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute {
        public string ParameterName { get; } = parameterName;
    }
}
#endif
