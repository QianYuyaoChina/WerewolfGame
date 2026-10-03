namespace ClientGUI.Tests;

/// <summary>极简断言与用例收集器，避免依赖外部测试框架。</summary>
public static class Check
{
    private static readonly List<string> Failures = new();
    private static int _passed;

    public static int Passed => _passed;
    public static IReadOnlyList<string> FailureList => Failures;

    public static void That(bool condition, string what)
    {
        if (condition) _passed++;
        else Failures.Add(what);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) _passed++;
        else Failures.Add($"{what}：期望 <{expected}>，实际 <{actual}>");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
    {
        var e = expected.ToArray();
        var a = actual.ToArray();
        if (e.SequenceEqual(a)) _passed++;
        else Failures.Add($"{what}：期望 [{string.Join(",", e)}]，实际 [{string.Join(",", a)}]");
    }

    public static void True(bool value, string what) => That(value, what + "（应为 true）");
    public static void False(bool value, string what) => That(!value, what + "（应为 false）");
}
