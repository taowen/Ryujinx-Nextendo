using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Buffers.Binary;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    partial class DrawManager
    {
        // Developer-only, explicitly enabled, one requested draw at a time.
        // Shader identities are complete guest byte sequences supplied privately.
        private readonly string _referenceDrawDirectory = Environment.GetEnvironmentVariable("ARCTRL_REFERENCE_DRAW_DIR");
        private readonly bool _referenceAllCbufs = Environment.GetEnvironmentVariable("ARCTRL_REFERENCE_CAPTURE_ALL_CBUFS") == "1";
        private readonly bool _referenceFullCbufs = Environment.GetEnvironmentVariable("ARCTRL_REFERENCE_CAPTURE_FULL_CBUFS") == "1";
        private readonly bool _referenceVertexOnly = Environment.GetEnvironmentVariable("ARCTRL_REFERENCE_MATCH_VERTEX_ONLY") == "1";
        private byte[] _referenceVs;
        private byte[] _referenceFs;

        private void CaptureReferenceDrawInputs(int count, int instanceCount)
        {
            if (string.IsNullOrEmpty(_referenceDrawDirectory)) return;
            string request = Path.Combine(_referenceDrawDirectory, "draw-capture.request");
            if (!File.Exists(request)) return;

            _referenceVs ??= File.ReadAllBytes(Path.Combine(_referenceDrawDirectory, "target-vs.bin"));
            if (!_referenceVertexOnly)
                _referenceFs ??= File.ReadAllBytes(Path.Combine(_referenceDrawDirectory, "target-fs.bin"));
            ulong shaderBase = _state.State.ShaderBaseAddress.Pack();
            ulong vsAddress = shaderBase + _state.State.ShaderState[1].Offset;
            ulong fsAddress = shaderBase + _state.State.ShaderState[5].Offset;
            // Guest code can be replaced at the same address between draws.
            bool match = _channel.MemoryManager.GetSpan(vsAddress, _referenceVs.Length).SequenceEqual(_referenceVs)
                && (_referenceVertexOnly || _channel.MemoryManager.GetSpan(fsAddress, _referenceFs.Length).SequenceEqual(_referenceFs));
            if (!match) return;

            string output = Path.Combine(_referenceDrawDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(output);
            var buffers = new List<object>();
            void SaveCbuf(int stage, int slot)
            {
                ulong address = _channel.BufferManager.GetGraphicsUniformBufferAddress(stage, slot);
                int size = _channel.BufferManager.GetGraphicsUniformBufferSize(stage, slot);
                if (size <= 0 || size > 65536) throw new InvalidDataException("Invalid reference cbuf size.");
                string file = $"stage{stage}-cbuf{slot}.bin";
                int snapshotBytes = _referenceAllCbufs && !_referenceFullCbufs ? Math.Min(size, 16) : size;
                try
                {
                    byte[] bytes = _channel.MemoryManager.Physical.GetSpan(address, snapshotBytes, tracked: true).ToArray();
                    File.WriteAllBytes(Path.Combine(output, file), bytes);
                    buffers.Add(new {stage, slot, address, size, snapshotBytes, file, error = (string)null});
                }
                catch (Exception error) when (_referenceAllCbufs)
                {
                    buffers.Add(new {stage, slot, address, size, snapshotBytes = 0, file = (string)null, error = error.GetType().Name});
                }
            }
            ulong globalAddress = 0;
            uint globalSize = 0;
            if (_referenceAllCbufs)
            {
                foreach (int stage in new[] { 0, 4 })
                {
                    uint mask = _channel.BufferManager.GetGraphicsUniformBufferUseMask(stage);
                    for (int slot = 0; slot < 16; slot++)
                        if ((mask & (1u << slot)) != 0) SaveCbuf(stage, slot);
                }
            }
            else
            {
                SaveCbuf(0, 0);
                SaveCbuf(0, 4);
                SaveCbuf(0, 11);
                SaveCbuf(4, 4);
                SaveCbuf(4, 9);
                SaveCbuf(4, 13);
                ulong c0Address = _channel.BufferManager.GetGraphicsUniformBufferAddress(0, 0);
                byte[] descriptor = _channel.MemoryManager.Physical.GetSpan(c0Address + 336, 12, tracked: true).ToArray();
                globalAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor);
                globalSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(8));
                if (globalSize == 0 || globalSize > 1048576) throw new InvalidDataException("Invalid reference global buffer size.");
                File.WriteAllBytes(Path.Combine(output, "vertex-global.bin"),
                    _channel.MemoryManager.GetSpan(globalAddress, (int)globalSize, tracked: true).ToArray());
            }
            File.WriteAllText(Path.Combine(output, "draw.json"), JsonSerializer.Serialize(new {
                count, instanceCount, vsAddress, fsAddress, globalAddress, globalSize, buffers,
                allBoundCbufs = _referenceAllCbufs, vertexOnly = _referenceVertexOnly,
                scope = "tracked guest memory read before selected draw; not a Vulkan GPU resource readback or power measurement"
            }, new JsonSerializerOptions {WriteIndented = true}));
            File.Delete(request);
        }
    }
}
