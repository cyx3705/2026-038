using System.Reflection;
using System.Runtime.InteropServices;

namespace SolidEdgeMcp;

/// <summary>COM helpers .NET 8 no longer ships (GetActiveObject) plus late-bound value conversion.</summary>
internal static class ComInterop
{
    public const int MkEUnavailable = unchecked((int)0x800401E3);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    public static object GetActiveObject(string progId)
    {
        CLSIDFromProgID(progId, out var clsid);
        GetActiveObject(ref clsid, IntPtr.Zero, out var instance);
        return instance;
    }

    /// <summary>COM identity: two proxies refer to one object when their IUnknown pointers match.</summary>
    public static bool Same(object a, object b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (!Marshal.IsComObject(a) || !Marshal.IsComObject(b))
            return false;
        var left = Marshal.GetIUnknownForObject(a);
        var right = Marshal.GetIUnknownForObject(b);
        try
        {
            return left == right;
        }
        finally
        {
            Marshal.Release(left);
            Marshal.Release(right);
        }
    }

    /// <summary>
    /// Plain IDispatch call, for the few signatures the C# dynamic binder refuses to convert.
    /// Indices in <paramref name="byRef"/> are sent as VT_BYREF.
    /// </summary>
    public static object? Invoke(object target, string method, object?[] arguments, params int[] byRef)
    {
        ParameterModifier[]? modifiers = null;
        if (byRef.Length > 0)
        {
            var modifier = new ParameterModifier(arguments.Length);
            foreach (var index in byRef)
                modifier[index] = true;
            modifiers = [modifier];
        }
        return target.GetType().InvokeMember(
            method, BindingFlags.InvokeMethod, null, target, arguments, modifiers, null, null);
    }

    public static int Int(object value) => Convert.ToInt32(value);

    public static double Double(object value) => Convert.ToDouble(value);

    public static string Str(object? value) => Convert.ToString(value) ?? "";
}
