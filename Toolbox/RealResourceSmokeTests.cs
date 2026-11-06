using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.Loader;

namespace Toolbox
{
    // Loads real SV / ZA game resources fetched into a temporary CI directory.
    // Tests performed here are deliberately distinct from renderer / GPU validation.
    internal static class RealResourceSmokeTests
    {
        private static int failures;

        private static void Check(string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine("[FAIL] " + name + ": " + ex);
            }
        }

        private static string Fingerprint(FirstPlugin.TextureData texture)
        {
            return string.Join(":", texture.Texture.TextureData
                .SelectMany(level => level)
                .Select(bytes => Convert.ToHexString(SHA256.HashData(bytes))));
        }

        private static void TestActualBntx(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 256)
                throw new InvalidDataException("Real BNTX input unexpectedly small");

            var format = new FirstPlugin.BNTX
            {
                FilePath = path,
                FileName = Path.GetFileName(path)
            };
            using (var input = new MemoryStream(bytes, writable: false))
            {
                if (!format.Identify(input))
                    throw new InvalidDataException("Not identified as BNTX");
                input.Position = 0;
                format.Load(input);
            }

            if (format.Textures == null || format.Textures.Count == 0)
                throw new InvalidDataException("No actual textures decoded");

            var originals = format.Textures.Values.ToDictionary(
                t => t.Text, t => new
                {
                    t.Width, t.Height, t.Format, t.MipCount,
                    Fingerprint = Fingerprint(t)
                }, StringComparer.Ordinal);
            if (originals.Values.Any(t => t.Width == 0 || t.Height == 0 || t.MipCount == 0 ||
                                          string.IsNullOrWhiteSpace(t.Fingerprint)))
                throw new InvalidDataException("Missing texture dimensions / mipmaps / data");

            // Non-destructive metadata edit performed only on an in-memory copy.
            var first = format.Textures.Values.First();
            string oldName = first.Text;
            string changedName = oldName + "_NET10_ROUNDTRIP";
            first.Text = changedName;

            using var output = new MemoryStream();
            format.Save(output);
            byte[] saved = output.ToArray();
            if (saved.Length < 256)
                throw new InvalidDataException("Real BNTX save produced too few bytes");

            var verify = new FirstPlugin.BNTX
            {
                FilePath = path,
                FileName = Path.GetFileName(path)
            };
            using (var reload = new MemoryStream(saved, writable: false))
            {
                if (!verify.Identify(reload))
                    throw new InvalidDataException("Saved BNTX signature invalid");
                reload.Position = 0;
                verify.Load(reload);
            }
            if (verify.Textures.Count != originals.Count)
                throw new InvalidDataException("Texture count changed after metadata edit");

            foreach (var original in originals)
            {
                string expectedName = original.Key == oldName ? changedName : original.Key;
                if (!verify.Textures.TryGetValue(expectedName, out var updated))
                    throw new InvalidDataException("Missing texture after edit: " + expectedName);
                var meta = original.Value;
                if (meta.Width != updated.Width || meta.Height != updated.Height ||
                    meta.Format != updated.Format || meta.MipCount != updated.MipCount ||
                    meta.Fingerprint != Fingerprint(updated))
                    throw new InvalidDataException("Texture metadata or mip payload changed: " + expectedName);
            }
            Console.WriteLine("Real BNTX: textures=" + originals.Count + ", bytes=" + bytes.Length +
                              ", saved=" + saved.Length + ", renamed=" + changedName);
        }

        private static void TestTrinityBoundary(string folder)
        {
            // These are Trinity model / material / skeleton / animation inputs, NOT BFRES.
            // Do not misrepresent an unsupported format as a successful edit.
            foreach (var extension in new[] { "trmdl", "trmsh", "trmmt", "trskl", "tranm" })
            {
                string path = Path.Combine(folder, "pm0001." + extension);
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 16)
                    throw new InvalidDataException("Trinity fixture too small: " + path);
                using var input = new MemoryStream(bytes, writable: false);
                if (new FirstPlugin.BFRES().Identify(input))
                    throw new InvalidDataException("Trinity file incorrectly recognized as BFRES");
                input.Position = 0;
                if (new FirstPlugin.BNTX().Identify(input))
                    throw new InvalidDataException("Trinity file incorrectly recognized as BNTX");
                Console.WriteLine("[UNSUPPORTED] Native Trinity edit: " + Path.GetFileName(path) +
                                  " (" + bytes.Length + " bytes)");
            }
        }

        internal static int Run()
        {
            string root = Environment.GetEnvironmentVariable("NET10_REAL_FIXTURES_DIR");
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Console.Error.WriteLine("NET10_REAL_FIXTURES_DIR must point to the CI-downloaded SV/ZA samples.");
                return 1;
            }

            Toolbox.Library.Runtime.ExecutableDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            // The regular application uses the historical Lib/ folder assembly resolver.
            // The standalone validation command must use the same dependency search path.
            AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
            {
                string filename = Path.Combine(AppContext.BaseDirectory, "Lib", assemblyName.Name + ".dll");
                return File.Exists(filename) ? context.LoadFromAssemblyPath(Path.GetFullPath(filename)) : null;
            };
            foreach (var game in new[] { "SV", "ZA" })
            {
                string folder = Path.Combine(root, game);
                if (!Directory.Exists(folder))
                {
                    if (game == "ZA")
                    {
                        Console.WriteLine("[SKIP] ZA_Resources is private; no authorized CI fixture was provided.");
                        continue;
                    }
                    failures++;
                    Console.Error.WriteLine("[FAIL] Mandatory public SV fixtures were not downloaded.");
                    continue;
                }
                foreach (var pokemon in new[] { "pm0001", "pm0025" })
                {
                    string fixture = Path.Combine(folder, pokemon + ".bntx");
                    Check(game + " real " + pokemon + " BNTX rename/save/reload/mips", () =>
                        TestActualBntx(fixture));
                }
                Check(game + " real pm0001 Trinity format boundary", () => TestTrinityBoundary(folder));
            }
            Console.WriteLine("Real resource smoke failures: " + failures);
            return failures == 0 ? 0 : 1;
        }
    }
}
