#if DEBUG
namespace NewAxis.Services;

public static partial class Debug
{
    
    static partial void AttachImpl();

    // You can call this method to find your code location by
    // looking the XREFs in the NativeAOT build, usefull only
    // for debugging NativeAOT errors only, usually you can
    // just debug the runtime build.
    public static void Attach() => AttachImpl();
}
#endif