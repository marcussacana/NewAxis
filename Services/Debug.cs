namespace NewAxis.Services;

public static partial class Debug
{
    static partial void AttachImpl();

    public static void Attach() => AttachImpl();
}