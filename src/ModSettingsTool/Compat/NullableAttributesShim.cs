// Build-compat shim, NOT a runtime type. The IL2CPP-generated Il2Cppmscorlib.dll declares
// System.Runtime.CompilerServices.NullableAttribute / NullableContextAttribute WITHOUT a
// compiler-usable constructor. With <Nullable>enable</Nullable> the C# compiler binds to those
// broken definitions and fails to emit nullable metadata for every annotated member
// (CS0656 "Missing compiler required member 'System.Runtime.CompilerServices.NullableAttribute..ctor'").
//
// Defining the attributes here, in source, makes Roslyn use THESE (source wins over imported
// metadata) and emit nullable info correctly. They are `internal`, so they never leak into any
// public surface. The resulting CS0436 ("conflicts with the imported type") is suppressed in
// ModSettingsTool.csproj via NoWarn; it is the expected, documented signal that the source
// definition is being used. Same pattern as the sibling mods' Compat shim.

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Event | AttributeTargets.Field |
        AttributeTargets.GenericParameter | AttributeTargets.Parameter |
        AttributeTargets.Property | AttributeTargets.ReturnValue,
        AllowMultiple = false, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte flag) => NullableFlags = new[] { flag };
        public NullableAttribute(byte[] flags) => NullableFlags = flags;
    }

    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Interface |
        AttributeTargets.Method | AttributeTargets.Module | AttributeTargets.Struct,
        AllowMultiple = false, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte flag) => Flag = flag;
    }
}
