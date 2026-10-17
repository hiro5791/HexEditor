namespace TestLib;

/// <summary>ライブラリのバージョンを返す (2 つのプラグインが別のバージョンを同時に使えることを確かめる)。</summary>
public static class TestLibInfo
{
#if TESTLIB_V1
    public static string Version => "TestLib 1.0.0";
#else
    public static string Version => "TestLib 2.0.0";
#endif
}
