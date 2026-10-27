using System.IO;
using System;
using System.Security.Cryptography;
using System.Text.Json;
using Ryujinx.Graphics.Shader;

namespace Ryujinx.Graphics.Gpu.Shader
{
    /// <summary>
    /// Shader dumper, writes binary shader code to disk.
    /// </summary>
    class ShaderDumper
    {
        private string _runtimeDir;
        private string _dumpPath;

        /// <summary>
        /// Current index of the shader dump binary file.
        /// This is incremented after each save, in order to give unique names to the files.
        /// </summary>
        public int CurrentDumpIndex { get; private set; }

        /// <summary>
        /// Creates a new instance of the shader dumper.
        /// </summary>
        public ShaderDumper()
        {
            CurrentDumpIndex = 1;
        }

        /// <summary>
        /// Dumps shader code to disk.
        /// </summary>
        /// <param name="code">Code to be dumped</param>
        /// <param name="compute">True for compute shader code, false for graphics shader code</param>
        /// <returns>Paths where the shader code was dumped</returns>
        public ShaderDumpPaths Dump(byte[] code, bool compute)
        {
            _dumpPath = GraphicsConfig.ShadersDumpPath;

            if (string.IsNullOrWhiteSpace(_dumpPath))
            {
                return default;
            }

            string fileName = "Shader" + CurrentDumpIndex.ToString("d4") + ".bin";

            string fullPath = Path.Combine(FullDir(), fileName);
            string codePath = Path.Combine(CodeDir(), fileName);

            CurrentDumpIndex++;

            using MemoryStream stream = new(code);
            BinaryReader codeReader = new(stream);

            using FileStream fullFile = File.Create(fullPath);
            using FileStream codeFile = File.Create(codePath);
            BinaryWriter fullWriter = new(fullFile);
            BinaryWriter codeWriter = new(codeFile);

            int headerSize = compute ? 0 : 0x50;

            fullWriter.Write(codeReader.ReadBytes(headerSize));

            byte[] temp = codeReader.ReadBytes(code.Length - headerSize);

            fullWriter.Write(temp);
            codeWriter.Write(temp);

            // Align to meet nvdisasm requirements.
            while (codeFile.Length % 0x20 != 0)
            {
                codeWriter.Write(0);
            }

            return new ShaderDumpPaths(fullPath, codePath);
        }

        // Preserve the emitted variant beside its exact guest bytes. Guest code
        // alone does not identify specialization or descriptor/input state.
        public static void SaveTranslation(ShaderDumpPaths paths, ShaderProgram program,
            byte[] cb1Data, string pairedVertexPath = null, GpuChannelComputeState? computeState = null)
        {
            if (!paths.HasPath) return;
            byte[] emitted = program.BinaryCode;
            string emittedPath = paths.FullPath + (emitted != null ? ".spv" : ".glsl");
            if (emitted != null) File.WriteAllBytes(emittedPath, emitted);
            else File.WriteAllText(emittedPath, program.Code ?? "");
            File.WriteAllBytes(paths.FullPath + ".cb1.bin", cb1Data);
            using FileStream file = File.Create(paths.FullPath + ".json");
            using Utf8JsonWriter json = new(file, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            json.WriteString("stage", program.Info.Stage.ToString());
            json.WriteString("language", program.Language.ToString());
            json.WriteString("guestSha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths.FullPath))));
            json.WriteString("emittedFile", Path.GetFileName(emittedPath));
            json.WriteString("emittedSha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(emittedPath))));
            json.WriteString("pairedVertexFile", pairedVertexPath == null ? null : Path.GetFileName(pairedVertexPath));
            json.WriteNumber("cb1Bytes", cb1Data.Length);
            // Preserve the exact guest-resource mapping behind each emitted
            // Vulkan binding so an offline replay never guesses slot order.
            json.WriteStartArray("constantBuffers");
            foreach (BufferDescriptor buffer in program.Info.CBuffers)
            {
                json.WriteStartObject();
                json.WriteNumber("set", buffer.Set);
                json.WriteNumber("binding", buffer.Binding);
                json.WriteNumber("slot", buffer.Slot);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("storageBuffers");
            foreach (BufferDescriptor buffer in program.Info.SBuffers)
            {
                json.WriteStartObject();
                json.WriteNumber("set", buffer.Set);
                json.WriteNumber("binding", buffer.Binding);
                json.WriteNumber("slot", buffer.Slot);
                json.WriteNumber("pointerCbufSlot", buffer.SbCbSlot);
                json.WriteNumber("pointerCbufOffset", buffer.SbCbOffset);
                json.WriteNumber("pointerCbufByteOffset", buffer.SbCbOffset * 4);
                json.WriteString("flags", buffer.Flags.ToString());
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("textures");
            foreach (TextureDescriptor texture in program.Info.Textures)
            {
                json.WriteStartObject();
                json.WriteNumber("set", texture.Set);
                json.WriteNumber("binding", texture.Binding);
                json.WriteNumber("cbufSlot", texture.CbufSlot);
                json.WriteNumber("handleIndex", texture.HandleIndex);
                json.WriteNumber("arrayLength", texture.ArrayLength);
                json.WriteBoolean("separate", texture.Separate);
                json.WriteString("type", texture.Type.ToString());
                json.WriteString("format", texture.Format.ToString());
                json.WriteString("flags", texture.Flags.ToString());
                json.WriteEndObject();
            }
            json.WriteEndArray();
            if (computeState is GpuChannelComputeState compute)
            {
                json.WriteStartObject("computeState");
                json.WriteStartArray("localSize");
                json.WriteNumberValue(compute.LocalSizeX);
                json.WriteNumberValue(compute.LocalSizeY);
                json.WriteNumberValue(compute.LocalSizeZ);
                json.WriteEndArray();
                json.WriteNumber("localMemoryBytes", compute.LocalMemorySize);
                json.WriteNumber("sharedMemoryBytes", compute.SharedMemorySize);
                json.WriteBoolean("hasUnalignedStorageBuffer", compute.HasUnalignedStorageBuffer);
                json.WriteString("source", "runtime QMD compute state used by GpuAccessor");
                json.WriteEndObject();
            }
            json.WriteString("scope", "translation capture; does not include complete draw resources or pipeline state");
            json.WriteEndObject();
        }

        /// <summary>
        /// Returns the output directory for shader code with header.
        /// </summary>
        /// <returns>Directory path</returns>
        private string FullDir()
        {
            return CreateAndReturn(Path.Combine(DumpDir(), "Full"));
        }

        /// <summary>
        /// Returns the output directory for shader code without header.
        /// </summary>
        /// <returns>Directory path</returns>
        private string CodeDir()
        {
            return CreateAndReturn(Path.Combine(DumpDir(), "Code"));
        }

        /// <summary>
        /// Returns the full output directory for the current shader dump.
        /// </summary>
        /// <returns>Directory path</returns>
        private string DumpDir()
        {
            if (string.IsNullOrEmpty(_runtimeDir))
            {
                int index = 1;

                do
                {
                    _runtimeDir = Path.Combine(_dumpPath, "Dumps" + index.ToString("d2"));

                    index++;
                }
                while (Directory.Exists(_runtimeDir));

                Directory.CreateDirectory(_runtimeDir);
            }

            return _runtimeDir;
        }

        /// <summary>
        /// Creates a new specified directory if needed.
        /// </summary>
        /// <param name="dir">The directory to create</param>
        /// <returns>The same directory passed to the method</returns>
        private static string CreateAndReturn(string dir)
        {
            Directory.CreateDirectory(dir);

            return dir;
        }
    }
}
