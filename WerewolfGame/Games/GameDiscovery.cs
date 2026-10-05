using System.Reflection;

namespace Werewolf.Server.Games;

/// <summary>
/// 反射发现：扫描程序集内所有「可供反射构造（公共无参构造）」的 <see cref="IGameModule"/> 实现。
/// 目的：新增游戏只需「新增一个文件」，组合根 <c>Program.cs</c> 不需要改。
/// </summary>
public static class GameDiscovery
{
    /// <summary>发现并实例化给定程序集内的所有游戏模块（跳过抽象 / 接口 / 无法构造的类型）。</summary>
    public static IEnumerable<IGameModule> Discover(params Assembly[] assemblies)
    {
        var found = new List<IGameModule>();
        if (assemblies == null) return found;

        foreach (var assembly in assemblies)
        {
            if (assembly == null) continue;

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                // 部分类型加载失败时，仍尝试使用成功加载的那些。
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IGameModule).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                try
                {
                    if (Activator.CreateInstance(type) is IGameModule module) found.Add(module);
                }
                catch
                {
                    // 单个模块构造失败不应影响其他模块的发现。
                }
            }
        }

        return found;
    }
}
