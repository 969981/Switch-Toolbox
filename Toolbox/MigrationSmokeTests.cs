using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace Toolbox
{
    // This deliberately bypasses WinForms' event loop. It validates the published
    // assembly layout and deterministic, empty format round-trips on a Windows runner.
    // It cannot substitute for interactive OpenGL rendering or real game samples.
    internal static class MigrationSmokeTests
    {
        private static int _failures;
        private static void Check(string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception error)
            {
                _failures++;
                Console.Error.WriteLine("[FAIL] " + name + ": " + error);
            }
        }

        internal static int Run()
        {
            var root = AppContext.BaseDirectory;
            Toolbox.Library.Runtime.ExecutableDir = root.TrimEnd(Path.DirectorySeparatorChar);
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                var file = Path.Combine(root, "Lib", name.Name + ".dll");
                return File.Exists(file) ? context.LoadFromAssemblyPath(Path.GetFullPath(file)) : null;
            };
            Check("Windows runtime / published managed dependencies", () =>
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                if (!File.Exists(Path.Combine(root, "Toolbox.Library.dll"))) throw new FileNotFoundException("Toolbox.Library.dll");
                if (!File.Exists(Path.Combine(root, "FirstPlugin.Plg.dll")) &&
                    !File.Exists(Path.Combine(root, "Lib", "Plugins", "FirstPlugin.Plg.dll")))
                    throw new FileNotFoundException("FirstPlugin.Plg.dll");
            });
            Check("Plugin discovery with current runtime", () =>
            {
                var dll = Path.Combine(root, "Lib", "Plugins", "FirstPlugin.Plg.dll");
                if (!File.Exists(dll)) throw new FileNotFoundException(dll);
                var asm = Assembly.LoadFrom(dll);
                foreach (string name in new[] { "FirstPlugin.BNTX", "FirstPlugin.BFRES" })
                    if (asm.GetType(name, true) == null) throw new TypeLoadException(name);
                var plugins = Toolbox.Library.PluginLoader.LoadPlugins();
                if (plugins.Count == 0) throw new Exception("No plugin instances discovered");
            });
            Check("BNTX create / identify / save / reload", () =>
            {
                byte[] created = FirstPlugin.BNTX.CreateNewBNTX("MigrationSmoke");
                if (created.Length < 64) throw new InvalidDataException("BNTX too small");
                var format = new FirstPlugin.BNTX();
                using (var input = new MemoryStream(created))
                {
                    if (!format.Identify(input)) throw new InvalidDataException("BNTX signature invalid");
                    input.Position = 0;
                    format.Load(input);
                }
                using var output = new MemoryStream();
                format.Save(output);
                if (output.Length < 64) throw new InvalidDataException("BNTX output empty");
                var next = new FirstPlugin.BNTX();
                using var check = new MemoryStream(output.ToArray());
                if (!next.Identify(check)) throw new InvalidDataException("BNTX round-trip signature invalid");
                check.Position = 0;
                next.Load(check);
            });
            Check("BFRES create / identify / save / reload", () =>
            {
                var create = typeof(FirstPlugin.BFRES).GetMethod("CreateNewBFRESSwitch", BindingFlags.NonPublic | BindingFlags.Static);
                if (create == null) throw new MissingMethodException("CreateNewBFRESSwitch");
                byte[] bytes = (byte[])create.Invoke(null, new object[] { "MigrationSmoke.bfres" });
                if (bytes.Length < 64) throw new InvalidDataException("BFRES too small");
                var format = new FirstPlugin.BFRES();
                using (var input = new MemoryStream(bytes))
                {
                    if (!format.Identify(input)) throw new InvalidDataException("BFRES signature invalid");
                    input.Position = 0;
                    format.Load(input);
                }
                using var output = new MemoryStream();
                format.Save(output);
                if (output.Length < 64) throw new InvalidDataException("BFRES output empty");
                using var check = new MemoryStream(output.ToArray());
                if (!(new FirstPlugin.BFRES()).Identify(check)) throw new InvalidDataException("BFRES round-trip signature invalid");
            });
            Console.WriteLine("Migration smoke failures: " + _failures);
            return _failures == 0 ? 0 : 1;
        }
    }
