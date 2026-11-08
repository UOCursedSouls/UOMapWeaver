using System.Reflection;
using System.Reflection.Emit;
using UOMapWeaver.Core.Plugins;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.Tests;

public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uomw-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void PluginLoader_FindsPluginInPluginsFolder()
    {
        WritePlugin(_root, "Alpha", "alpha", WorldFormatContract.Version);

        var plugins = PluginLoader.Load([_root]);

        var p = Assert.Single(plugins);
        Assert.Equal("alpha", p.Id);
        Assert.Equal("Alpha plugin", p.DisplayName);
        Assert.Equal(WorldFormatCapabilities.Import | WorldFormatCapabilities.Export, p.Capabilities);
    }

    [Fact]
    public void PluginLoader_RefusesContractVersion2()
    {
        WritePlugin(_root, "Future", "future", 2);
        var log = new List<string>();

        Assert.Empty(PluginLoader.Load([_root], log.Add));
        Assert.Contains(log, m => m.Contains("contract v2"));
    }

    [Fact]
    public void PluginLoader_DuplicateId_KeepsFirst()
    {
        WritePlugin(_root, "A1", "same", 1);
        WritePlugin(_root, "B2", "same", 1);
        var log = new List<string>();

        var p = Assert.Single(PluginLoader.Load([_root], log.Add));
        Assert.Contains("A1", p.GetType().Assembly.Location);
        Assert.Contains(log, m => m.Contains("already loaded"));
    }

    [Fact]
    public void PluginLoader_BrokenDll_SkippedOthersLoad()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Broken"));
        File.WriteAllText(Path.Combine(_root, "Broken", "Broken.dll"), "not a dll");
        WritePlugin(_root, "Good", "good", 1);
        var log = new List<string>();

        var p = Assert.Single(PluginLoader.Load([Path.Combine(_root, "missing"), _root], log.Add));
        Assert.Equal("good", p.Id);
        Assert.Contains(log, m => m.Contains("Broken.dll"));
    }

    [Fact]
    public void PluginLoader_UnreadableFolder_SkippedOthersLoad()
    {
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        WritePlugin(_root, "Good", "good", 1);
        var log = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(locked, UnixFileMode.None);
        }

        try
        {
            var p = Assert.Single(PluginLoader.Load([locked, _root], log.Add));
            Assert.Equal("good", p.Id);
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>Emits a plugin assembly on disk: a public class implementing IWorldFormatPlugin with constant properties.</summary>
    private static void WritePlugin(string folder, string name, string id, int contractVersion)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var tb = ab.DefineDynamicModule(name).DefineType(name + ".Plugin", TypeAttributes.Public | TypeAttributes.Class, typeof(object), [typeof(IWorldFormatPlugin)]);
        tb.DefineDefaultConstructor(MethodAttributes.Public);

        void Prop(string prop, Type type, Action<ILGenerator> load)
        {
            var m = tb.DefineMethod("get_" + prop, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.SpecialName, type, Type.EmptyTypes);
            var il = m.GetILGenerator();
            load(il);
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(m, typeof(IWorldFormatPlugin).GetProperty(prop)!.GetMethod!);
        }

        void Throws(string method, Type ret, Type[] args)
        {
            var m = tb.DefineMethod(method, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot, ret, args);
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Throw);
            tb.DefineMethodOverride(m, typeof(IWorldFormatPlugin).GetMethod(method)!);
        }

        Prop("ContractVersion", typeof(int), il => il.Emit(OpCodes.Ldc_I4, contractVersion));
        Prop("Id", typeof(string), il => il.Emit(OpCodes.Ldstr, id));
        Prop("DisplayName", typeof(string), il => il.Emit(OpCodes.Ldstr, name + " plugin"));
        Prop("Capabilities", typeof(WorldFormatCapabilities), il => il.Emit(OpCodes.Ldc_I4_3));
        Throws("CreateWorld", typeof(IWorldWriter), [typeof(string), typeof(WorldCreateOptions)]);
        Throws("OpenWorld", typeof(IWorldReader), [typeof(string)]);
        tb.CreateType();

        var dir = Path.Combine(folder, name);
        Directory.CreateDirectory(dir);
        ab.Save(Path.Combine(dir, name + ".dll"));
    }
}

public class WorldBlockTests
{
    private static WorldLandTile[] Land(int n) => new WorldLandTile[n];

    [Fact]
    public void WorldBlock_63LandTiles_Refused() =>
        Assert.Throws<ArgumentException>(() => new WorldBlock(0, 0, Land(63), []));

    [Fact]
    public void WorldBlock_StaticAtX8_Refused() =>
        Assert.Throws<ArgumentException>(() => new WorldBlock(0, 0, Land(64), [new WorldStaticTile(1, 8, 0, 0, 0)]));

    [Fact]
    public void WorldBlock_ValidBlock_Accepted()
    {
        var b = new WorldBlock(3, 4, Land(64), [new WorldStaticTile(1, 7, 7, -5, 0)]);
        Assert.Equal((3, 4), (b.BlockX, b.BlockY));
    }
}
