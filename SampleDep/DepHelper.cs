namespace SampleDep
{
    public static class DepHelper
    {
#if V2
        public static string Describe() => "SampleDep v2";
#else
        public static string Describe() => "SampleDep v1";
#endif
    }
}