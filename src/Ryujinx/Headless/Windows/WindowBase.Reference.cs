using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.HLE.HOS.Services.Hid;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Headless
{
    // Opt-in developer control. It reads only fixed command filenames in a local
    // directory, never executes commands or changes guest graphics behavior.
    abstract unsafe partial class WindowBase
    {
        private string _referenceDirectory;
        private long _referenceNextPoll;
        private long _referenceNextStatus;
        private int _referenceCapture;
        private long _referencePresentedFrames;
        private ControllerKeys _referenceButtons;
        private long _referenceInputUntil;

        private void InitializeReferenceAutomation()
        {
            _referenceDirectory = Environment.GetEnvironmentVariable("ARCTRL_REFERENCE_DIR");
            if (string.IsNullOrEmpty(_referenceDirectory)) return;
            _referenceDirectory = Path.GetFullPath(_referenceDirectory);
            Directory.CreateDirectory(_referenceDirectory);
            Renderer.ScreenCaptured += SaveReferenceFrame;
        }

        // The opt-in reference run feeds its own normal HID samples, independent
        // of desktop focus. A bounded button hold always releases automatically.
        private bool UpdateReferenceInput()
        {
            if (string.IsNullOrEmpty(_referenceDirectory)) return false;
            ControllerKeys buttons = Stopwatch.GetTimestamp() < _referenceInputUntil
                ? _referenceButtons : (ControllerKeys)0;
            Device.Hid.Npads.Update(new GamepadInput[]
            {
                new() { PlayerId = PlayerIndex.Player1, Buttons = buttons },
            });
            return true;
        }

        private void CaptureReferenceFrame()
        {
            if (string.IsNullOrEmpty(_referenceDirectory)) return;
            Interlocked.Increment(ref _referencePresentedFrames);
            if (Interlocked.Exchange(ref _referenceCapture, 0) != 0)
                Renderer.Screenshot();
        }

        private void PollReferenceAutomation()
        {
            if (string.IsNullOrEmpty(_referenceDirectory)) return;
            long now = Stopwatch.GetTimestamp();
            if (now < _referenceNextPoll) return;
            _referenceNextPoll = now + Stopwatch.Frequency / 10;
            try
            {
                string input = Path.Combine(_referenceDirectory, "input.request");
                if (File.Exists(input))
                {
                    try
                    {
                        using JsonDocument request = JsonDocument.Parse(File.ReadAllText(input));
                        string name = request.RootElement.GetProperty("buttons").GetString();
                        int duration = request.RootElement.GetProperty("durationMs").GetInt32();
                        if (!Enum.TryParse(name, out ControllerKeys buttons) || duration < 1 || duration > 3000)
                            throw new ArgumentException("Invalid reference button hold.");
                        _referenceButtons = buttons;
                        _referenceInputUntil = now + duration * Stopwatch.Frequency / 1000;
                    }
                    catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
                    {
                        Logger.Warning?.Print(LogClass.Application, $"Reference input rejected: {exception.Message}");
                    }
                    finally { File.Delete(input); }
                }
                string capture = Path.Combine(_referenceDirectory, "capture.request");
                if (File.Exists(capture))
                {
                    File.Delete(capture);
                    Interlocked.Exchange(ref _referenceCapture, 1);
                }
                if (now >= _referenceNextStatus)
                {
                    _referenceNextStatus = now + Stopwatch.Frequency * 2;
                    using Process process = Process.GetCurrentProcess();
                    string temporary = Path.Combine(_referenceDirectory, "status.tmp");
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new
                    {
                        utc = DateTime.UtcNow,
                        pid = Environment.ProcessId,
                        titleId = Device.Processes.ActiveApplication?.ProgramIdText,
                        presentedFrames = Interlocked.Read(ref _referencePresentedFrames),
                        fps = FiniteOrNull(Device.Statistics.GetGameFrameRate()),
                        frameTimeMs = FiniteOrNull(Device.Statistics.GetGameFrameTime()),
                        cpuSeconds = process.TotalProcessorTime.TotalSeconds,
                        workingSetBytes = process.WorkingSet64,
                        gpu = Renderer.GetHardwareInfo().GpuDriver,
                    }));
                    File.Move(temporary, Path.Combine(_referenceDirectory, "status.json"), true);
                }
                string stop = Path.Combine(_referenceDirectory, "stop.request");
                if (File.Exists(stop))
                {
                    File.Delete(stop);
                    _gpuCancellationTokenSource.Cancel();
                    _isActive = false;
                }
            }
            catch (IOException exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"Reference control: {exception.Message}");
            }
        }

        private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;

        private void SaveReferenceFrame(object sender, ScreenCaptureImageInfo frame)
        {
            if (frame.Data.Length == 0 || frame.Width <= 0 || frame.Height <= 0) return;
            var color = frame.IsBgra ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
            using SKBitmap source = new(new SKImageInfo(frame.Width, frame.Height, color, SKAlphaType.Premul));
            Marshal.Copy(frame.Data, 0, source.GetPixels(), frame.Data.Length);
            using SKBitmap output = new(frame.Width, frame.Height);
            using (SKCanvas canvas = new(output))
            {
                canvas.Clear(SKColors.Black);
                canvas.SetMatrix(SKMatrix.CreateScale(frame.FlipX ? -1 : 1, frame.FlipY ? -1 : 1,
                    frame.Width / 2f, frame.Height / 2f));
                canvas.DrawBitmap(source, SKPoint.Empty);
            }
            string name = $"frame-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png";
            using SKData png = output.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream file = File.Create(Path.Combine(_referenceDirectory, name));
            png.SaveTo(file);
            Logger.Notice.Print(LogClass.Application, $"Reference frame saved: {name}");
        }
    }
}
