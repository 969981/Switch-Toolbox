using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
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
            Check("Published .NET 10 x64 runtime and updater", () =>
            {
                if (Environment.Version.Major != 10)
                    throw new InvalidOperationException("Expected .NET 10 runtime, found " + Environment.Version);
                if (!Environment.Is64BitProcess)
                    throw new InvalidOperationException("Windows x64 package started as 32-bit process");
                foreach (var filename in new[] { "Updater.exe", "Updater.dll", "Updater.runtimeconfig.json",
                                                  "Toolbox.exe", "Toolbox.runtimeconfig.json" })
                    if (!File.Exists(Path.Combine(root, filename)))
                        throw new FileNotFoundException(filename);
            });
            Check("File format registry initializes from discovered plugins", () =>
            {
                var formats = Toolbox.Library.FileManager.GetFileFormats();
                if (formats.Length < 2)
                    throw new InvalidDataException("File format registry is empty");
                if (!formats.Any(f => f is FirstPlugin.BFRES) ||
                    !formats.Any(f => f is FirstPlugin.BNTX))
                    throw new InvalidDataException("BFRES/BNTX formats missing from registration");
                Console.WriteLine("Registered file formats: " + formats.Length);
            });
            Check("DockPanelSuite 3.1.1 published bindings and actual loader identity", () =>
            {
                foreach (string filename in new[]
                {
                    "WeifenLuo.WinFormsUI.Docking.dll",
                    "WeifenLuo.WinFormsUI.Docking.ThemeVS2015.dll"
                })
                {
                    string primary = Path.Combine(root, filename);
                    string fallback = Path.Combine(root, "Lib", filename);
                    if (!File.Exists(primary) || !File.Exists(fallback))
                        throw new FileNotFoundException("Expected root and Lib copies: " + filename);
                    byte[] canonicalHash = SHA256.HashData(File.ReadAllBytes(primary));
                    if (!canonicalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fallback))))
                        throw new InvalidDataException("DockPanelSuite binary mismatch: " + filename);

                    AssemblyName identity = AssemblyName.GetAssemblyName(primary);
                    Assembly resolved = AssemblyLoadContext.Default.LoadFromAssemblyName(identity);
                    if (!string.Equals(identity.FullName, resolved.GetName().FullName,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("DockPanelSuite assembly identity mismatch: " + filename);
                    if (resolved.GetExportedTypes().Length == 0)
                        throw new TypeLoadException("No public DockPanelSuite types: " + filename);
                    var candidates = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(assembly => string.Equals(assembly.GetName().Name,
                            identity.Name, StringComparison.Ordinal));
                    foreach (var candidate in candidates)
                    {
                        if (string.IsNullOrWhiteSpace(candidate.Location) ||
                            !File.Exists(candidate.Location) ||
                            !canonicalHash.SequenceEqual(SHA256.HashData(
                                File.ReadAllBytes(candidate.Location))))
                            throw new InvalidDataException("Unexpected loaded DockPanelSuite binary: " +
                                candidate.FullName + " from " + candidate.Location);
                    }
                    Console.WriteLine("DockPanelSuite runtime: " + identity.FullName +
                                      " from " + resolved.Location);
                }
            });
            Check("WinForms MainForm designer and control creation", () =>
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                using var form = new MainForm();
                if (form.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("MainForm handle was not created");
            });
            Check("DirectXTex native/managed bridge loads under .NET 10", () =>
            {
                var path = Path.Combine(root, "x64", "DirectXTexNetImpl.dll");
                if (!File.Exists(path)) throw new FileNotFoundException(path);
                var nativeBridge = Assembly.LoadFrom(path);
                if (nativeBridge == null)
                    throw new InvalidOperationException("DirectXTexNetImpl could not be loaded");
                Console.WriteLine("DirectXTex bridge: " + nativeBridge.FullName);
            });

            Check("Real bundled Yaz0/SZS asset decompression", () =>
            {
                // Game-derived fixture already present in the repository; never ship it in the release.
                var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
                if (string.IsNullOrEmpty(workspace))
                {
                    Console.WriteLine("[SKIP] Repository fixture not available outside CI workspace");
                    return;
                }
                var file = Path.Combine(workspace, "File_Format_Library", "BodyB_Ysi.szs");
                var compressed = File.ReadAllBytes(file);
                if (compressed.Length < 16 || Encoding.ASCII.GetString(compressed, 0, 4) != "Yaz0")
                    throw new InvalidDataException("Fixture must be a Yaz0 SZS file");
                uint expectedLength = ((uint)compressed[4] << 24) | ((uint)compressed[5] << 16) |
                                      ((uint)compressed[6] << 8) | compressed[7];
                using var decoded = new Toolbox.Library.Yaz0().Decompress(new MemoryStream(compressed));
                if (expectedLength == 0 || decoded.Length != expectedLength)
                    throw new InvalidDataException("Yaz0 decompressed size does not match header");
                Console.WriteLine("Yaz0 real fixture: " + compressed.Length + " -> " + decoded.Length);
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
                // The legacy Save method owns and closes the passed stream.
                byte[] saved = output.ToArray();
                if (saved.Length < 64) throw new InvalidDataException("BNTX output empty");
                var next = new FirstPlugin.BNTX();
                using var check = new MemoryStream(saved);
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
                // The BFRES UI wrapper requires an ObjectEditor/GL context. Exercise the
                // underlying parser and serializer without creating UI controls.
                var format = new FirstPlugin.BFRES();
                using (var input = new MemoryStream(bytes))
                {
                    if (!format.Identify(input)) throw new InvalidDataException("BFRES signature invalid");
                }
                var resolver = Assembly.LoadFrom(Path.Combine(root, "Lib", "Syroot.NintenTools.NSW.Bfres.dll"));
                var resourceType = resolver.GetType("Syroot.NintenTools.NSW.Bfres.ResFile", true);
                var constructor = resourceType.GetConstructors()
                    .FirstOrDefault(method => method.GetParameters().Length > 0 &&
                        method.GetParameters()[0].ParameterType == typeof(Stream));
                if (constructor == null) throw new MissingMethodException(resourceType.FullName, ".ctor(Stream, ...)");
                object Construct(Stream stream)
                {
                    var args = constructor.GetParameters().Select((parameter, index) =>
                        index == 0 ? (object)stream :
                        parameter.HasDefaultValue ? parameter.DefaultValue :
                        parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
                    return constructor.Invoke(args);
                }
                var parsed = Construct(new MemoryStream(bytes));
                var save = resourceType.GetMethods().FirstOrDefault(method => method.Name == "Save" &&
                    method.GetParameters().Length > 0 && method.GetParameters()[0].ParameterType == typeof(Stream));
                if (save == null) throw new MissingMethodException(resourceType.FullName, "Save(Stream, ...)");
                using var output = new MemoryStream();
                var saveArgs = save.GetParameters().Select((parameter, index) =>
                    index == 0 ? (object)output :
                    parameter.HasDefaultValue ? parameter.DefaultValue :
                    parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
                save.Invoke(parsed, saveArgs);
                byte[] saved = output.ToArray();
                if (saved.Length < 64) throw new InvalidDataException("BFRES output empty");
                using var check = new MemoryStream(saved);
                if (!(new FirstPlugin.BFRES()).Identify(check)) throw new InvalidDataException("BFRES round-trip signature invalid");
                Construct(new MemoryStream(saved));
            });
            Console.WriteLine("Migration smoke failures: " + _failures);
            return _failures == 0 ? 0 : 1;
        }
    }
}
