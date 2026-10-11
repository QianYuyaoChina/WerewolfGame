using System.Text;

namespace Werewolf.Server;

/// <summary>
/// 原子写盘（架构 K8 / D6）：先写同目录的临时文件，再 <c>File.Move(overwrite: true)</c> 替换。
///
/// 为什么不能直接 <c>File.WriteAllText</c>：写到一半断电 / 抛异常，磁盘上就留下**半份**内容，
/// 下一局读到坏词库。原子替换保证观察者要么看到旧内容、要么看到完整的新内容。
///
/// 失败契约：不抛异常，返回 (false, 人话原因)，并保证不留下临时文件。
/// </summary>
public static class AtomicFileWriter
{
    /// <summary>
    /// 把 <paramref name="content"/>（UTF-8，无 BOM）原子写入 <paramref name="path"/>。
    /// </summary>
    /// <returns>(是否成功, 失败时的人话原因)。</returns>
    public static (bool ok, string error) TryWrite(string path, string content)
    {
        if (string.IsNullOrWhiteSpace(path)) return (false, "无法写入：目标路径为空");

        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            tmp = $"{path}.tmp-{Guid.NewGuid():N}";
            File.WriteAllText(tmp, content ?? "", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // NTFS 上是原子替换：读方永远看不到「写了一半」的文件。
            File.Move(tmp, path, overwrite: true);
            tmp = null;   // 已成功搬走，不需要再删
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, "无法写入：" + ex.Message);
        }
        finally
        {
            if (tmp != null) TryDelete(tmp);
        }
    }

    /// <summary>尽力删除文件（失败静默 —— 收尾 / 清理路径不该再抛异常）。</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件删不掉不影响主流程：它带着随机后缀，不会被当成正式文件读到。
        }
    }
}
