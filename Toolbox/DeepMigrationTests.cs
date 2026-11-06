using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL;
using Toolbox.Library;

namespace Toolbox
{
    // Experimental, independently reported Windows GPU/codec/ABI gates.
    // Success must never be confused with the synthetic BFRES smoke test.
    internal static class DeepMigrationTests
    {
        private static int failures;
        private static void Check(string title, Action test)
        {
            try
            {
                test();
                Console.WriteLine("[DEEP PASS] " + title);
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine("[DEEP FAIL] " + title + ": " + error);
            }
        }

        private static byte[] DecodePixels(FirstPlugin.TextureData texture)
        {
            // Uses the real Switch deswizzle and pixel codec, not compressed-byte hashes.
            using var bitmap = texture.GetBitmap(0, 0);
            if (bitmap == null)
                throw new InvalidDataException("BNTX mip0 could not be decoded to a Bitmap. Format=" + texture.Format);
            if (bitmap.Width != texture.Width || bitmap.Height != texture.Height)
                throw new InvalidDataException("Decoded dimensions mismatch");
            return BitmapExtension.ImageToByte(bitmap);
        }

        private static void TestBlockPixelChange(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Required SV/ZA fixture is missing", path);
            byte[] bytes = File.ReadAllBytes(path);
            var doc = new FirstPlugin.BNTX { FilePath = path, FileName = Path.GetFileName(path) };
            using (var input = new MemoryStream(bytes, writable: false))
                doc.Load(input);
            var original = doc.Textures.Values.FirstOrDefault()
                ?? throw new InvalidDataException("Real BNTX has no textures");
            Console.WriteLine("[DEEP INFO] " + Path.GetFileName(path) + " format=" +
                              original.Format + " dimensions=" + original.Width + "x" + original.Height +
                              " mips=" + original.MipCount);

            // Copy one independently valid existing 16-byte compressed block to a
            // different location inside the same mip. This is a genuine change in
            // decoded texture pixels; it does NOT claim arbitrary ASTC encoding.
            if (!STGenericTexture.IsAtscFormat(original.Format) &&
                !original.Format.ToString().StartsWith("BC", StringComparison.Ordinal))
                throw new NotSupportedException("This test uses independent 16-byte ASTC/BCn blocks; found " +
                                                original.Format);
            if (original.Texture.TextureData.Count != 1 ||
                original.Texture.TextureData[0].Count == 0)
                throw new NotSupportedException("Fixture does not have an editable first surface/mip");
            int blockLength = original.Format.ToString().StartsWith("BC1", StringComparison.Ordinal) ||
                              original.Format.ToString().StartsWith("BC4", StringComparison.Ordinal) ? 8 : 16;
            var stored = original.Texture.TextureData[0][0];
            if (stored.Length < blockLength * 8)
                throw new InvalidDataException("Insufficient compressed data for a block mutation");
            byte[] beforePixels = DecodePixels(original);
            byte[] oldStored = (byte[])stored.Clone();
            int donorOffset = -1;
            // Prefer an internal block well away from the source to avoid overlapping.
            for (int offset = blockLength * 4; offset + blockLength <= stored.Length;
                 offset += blockLength)
            {
                if (!stored.AsSpan(0, blockLength).SequenceEqual(stored.AsSpan(offset, blockLength)))
                {
                    donorOffset = offset;
                    break;
                }
            }
            if (donorOffset < 0)
                throw new InvalidDataException("All candidate compressed blocks are identical");
            Array.Copy(stored, donorOffset, stored, 0, blockLength);
            byte[] expected = (byte[])stored.Clone();

            using var output = new MemoryStream();
            doc.Save(output);
            byte[] saved = output.ToArray();
            var reread = new FirstPlugin.BNTX { FilePath = path, FileName = Path.GetFileName(path) };
            using (var input = new MemoryStream(saved, writable: false))
                reread.Load(input);
            if (!reread.Textures.TryGetValue(original.Text, out var changed))
                throw new InvalidDataException("Texture disappeared during save/readback");
            if (changed.Width != original.Width || changed.Height != original.Height ||
                changed.MipCount != original.MipCount || changed.Format != original.Format)
                throw new InvalidDataException("Unexpected texture metadata change");
            if (!expected.SequenceEqual(changed.Texture.TextureData[0][0]))
                throw new InvalidDataException("Changed compressed block did not survive BNTX serialization");
            if (oldStored.SequenceEqual(changed.Texture.TextureData[0][0]))
                throw new InvalidDataException("Encoded texture has not changed");
            byte[] afterPixels = DecodePixels(changed);
            if (beforePixels.Length != afterPixels.Length)
                throw new InvalidDataException("RGBA dimensions changed");
            int changedBytes = 0;
            for (int i = 0; i < beforePixels.Length; i++)
                if (beforePixels[i] != afterPixels[i]) changedBytes++;
            if (changedBytes == 0)
                throw new InvalidDataException("Compressed data changed, but decoded pixels did not");
            Console.WriteLine("[DEEP PIXEL] format=" + original.Format + " donor=" + donorOffset +
                              " encodedBlock=" + blockLength + " rgbaBytesChanged=" + changedBytes +
                              " savedBytes=" + saved.Length);
        }

        private static void TestGlContext(bool requireShaders)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var host = new Form { Text = "net10-private-gl-test", Width = 128, Height = 128 };
            using var control = new OpenTK.GLControl { Dock = DockStyle.Fill };
            host.Controls.Add(control);
            host.CreateControl();
            control.CreateControl();
            control.MakeCurrent();
            string version = GL.GetString(StringName.Version);
            string renderer = GL.GetString(StringName.Renderer);
            if (string.IsNullOrEmpty(version))
                throw new NotSupportedException("No OpenGL context on CI Windows runner");
            GL.Viewport(0, 0, 64, 64);
            GL.ClearColor(0.125f, 0.25f, 0.5f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            var rgba = new byte[4];
            GL.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
            if (rgba[0] < 20 || rgba[0] > 50 ||
                rgba[1] < 45 || rgba[1] > 85 ||
                rgba[2] < 105 || rgba[2] > 150 || rgba[3] != 255)
                throw new InvalidDataException("OpenGL framebuffer readback mismatch: " +
                                               string.Join(",", rgba));
            Console.WriteLine("[DEEP GL] OpenGL=" + version + " renderer=" + renderer +
                              " clearReadback=" + string.Join(",", rgba));
            if (!requireShaders)
                return;

            int shader = GL.CreateShader(ShaderType.VertexShader);
            if (shader == 0)
                throw new NotSupportedException("GLSL shader creation unsupported by context: " + version);
            try
            {
                GL.ShaderSource(shader,
                    "#version 120\nattribute vec3 p; void main(){ gl_Position=vec4(p, 1.0); }");
                GL.CompileShader(shader);
                GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
                if (compiled != 1)
                    throw new InvalidDataException("GLSL vertex shader compilation failed: " +
                                                   GL.GetShaderInfoLog(shader));
            }
            finally { GL.DeleteShader(shader); }
            Console.WriteLine("[DEEP GL] GLSL vertex shader compiled");
        }

        private static void TestAssimpNative()
        {
            // Minimal real native importer invocation: checks DLL/ABI, not a game FBX.
            string file = Path.Combine(Path.GetTempPath(), "net10-assimp-abi-" +
                Guid.NewGuid().ToString("N") + ".stl");
            File.WriteAllText(file,
                "solid triangle\nfacet normal 0 0 1\n outer loop\n" +
                "vertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\n" +
                "endloop\nendfacet\nendsolid triangle\n");
            try
            {
                using var importer = new Assimp.AssimpContext();
                var scene = importer.ImportFile(file, Assimp.PostProcessSteps.Triangulate);
                if (scene == null || scene.MeshCount != 1 ||
                    scene.Meshes[0].VertexCount < 3 || scene.Meshes[0].FaceCount != 1)
                    throw new InvalidDataException("Assimp native STL importer returned invalid geometry");
                Console.WriteLine("[DEEP ABI] Assimp: one mesh, " +
                                  scene.Meshes[0].VertexCount + " vertices");
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        private static void TestSyrootRealBntx(string path)
        {
            // This test invokes the real Syroot parser directly, as a distinct
            // dependency gate from Switch-Toolbox's plugin wrapper.
            using var stream = File.OpenRead(path);
            var type = typeof(Syroot.NintenTools.NSW.Bntx.BntxFile);
            object resource = Activator.CreateInstance(type, new object[] { stream })
                ?? throw new InvalidDataException("Syroot BNTX parser returned null");
            var textures = type.GetProperty("Textures")?.GetValue(resource)
                ?? throw new MissingMemberException(type.FullName, "Textures");
            if (textures is not System.Collections.ICollection list || list.Count < 1)
                throw new InvalidDataException("Syroot parser did not read real textures");
            Console.WriteLine("[DEEP ABI] Syroot BNTX textureCount=" + list.Count);
        }

        internal static int Run()
        {
            failures = 0;
            string root = AppContext.BaseDirectory;
            Runtime.ExecutableDir = root.TrimEnd(Path.DirectorySeparatorChar);
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                string dll = Path.Combine(root, "Lib", name.Name + ".dll");
                return File.Exists(dll) ? context.LoadFromAssemblyPath(Path.GetFullPath(dll)) : null;
            };
            Check("OpenTK native GL context and framebuffer RGBA readback", () => TestGlContext(false));
            Check("OpenTK GLSL shader creation/compilation", () => TestGlContext(true));
            Check("AssimpNet managed + native triangulated STL import", TestAssimpNative);
            string fixtures = Environment.GetEnvironmentVariable("NET10_REAL_FIXTURES_DIR");
            if (string.IsNullOrWhiteSpace(fixtures))
            {
                failures++;
                Console.Error.WriteLine("[DEEP FAIL] NET10_REAL_FIXTURES_DIR required");
            }
            else
            {
                foreach (string game in new[] { "SV", "ZA" })
                {
                    string file = Path.Combine(fixtures, game, "pm0025.bntx");
                    Check(game + " real BNTX compressed block pixel edit/save/redecode", () =>
                        TestBlockPixelChange(file));
                    Check(game + " real native Syroot BNTX parse", () =>
                        TestSyrootRealBntx(file));
                }
            }
            Console.WriteLine("NET10_DEEP_TESTS failures=" + failures +
                              " (GL test is not game-model rendering; block edit is not ASTC PNG encoding)");
            return failures == 0 ? 0 : 1;
        }
    }
}
