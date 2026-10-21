using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Asterion.Core;

namespace Asterion.Zwo;

public sealed class ZwoAsiCameraBackend : ICameraBackend
{
    private const int Exposure = 1, Gain = 0, Rgb24 = 1;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CameraInfo? camera;
    private CameraSettings settings = new(100, 50);
    private CameraCapabilities? capabilities;
    private bool connected, capturing;
    private CameraConnectionState connection = CameraConnectionState.Disconnected;
    public event EventHandler<ImageFrame>? FrameReceived;

    public Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken ct) => Task.Run<IReadOnlyList<CameraDescriptor>>(() => {
        var n = Native.ASIGetNumOfConnectedCameras(); var result = new List<CameraDescriptor>();
        for (var i=0;i<n;i++) { Check(Native.ASIGetCameraProperty(out var c,i)); result.Add(Describe(c)); }
        return result;
    }, ct);

    public async Task ConnectAsync(string cameraId, CancellationToken ct) {
        await gate.WaitAsync(ct); try {
            if(!int.TryParse(cameraId,out var id)) throw new ArgumentException("Invalid ASI camera ID.");
            Check(Native.ASIGetCameraPropertyByID(id,out var c)); camera=c; connection=CameraConnectionState.Connecting;
            Check(Native.ASIOpenCamera(id)); try { Check(Native.ASIInitCamera(id)); } catch { Native.ASICloseCamera(id); camera=null; throw; }
            connected=true; capabilities=ReadCapabilities(c); settings=ReadSettings(); connection=CameraConnectionState.Connected;
            StartVideo();
        } catch { connection=CameraConnectionState.Faulted; throw; } finally { gate.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken ct) { await gate.WaitAsync(ct); try { if(camera is { } c) { if(capturing){Native.ASIStopVideoCapture(c.CameraID);capturing=false;} Native.ASICloseCamera(c.CameraID); } connected=false; camera=null; capabilities=null; connection=CameraConnectionState.Disconnected; } finally { gate.Release(); } }
    public Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(capabilities ?? throw new InvalidOperationException("Connect a camera first."));
    public Task<CameraSettings> GetSettingsAsync(CancellationToken ct) => Task.FromResult(ReadSettings());
    public async Task SetSettingsAsync(CameraSettings value,CancellationToken ct) { await gate.WaitAsync(ct); try { var c=RequireCamera(); if(capturing){Check(Native.ASIStopVideoCapture(c.CameraID));capturing=false;} Check(Native.ASISetControlValue(c.CameraID,Exposure,(int)Math.Round(value.ExposureMilliseconds*1000),0)); Check(Native.ASISetControlValue(c.CameraID,Gain,(int)Math.Round(value.Gain),0)); settings=value; StartVideo(); } finally { gate.Release(); } }
    public Task<ImageFrame> GetPreviewFrameAsync(CancellationToken ct) => GrabAsync(ct);
    public Task<ImageFrame> CaptureSnapshotAsync(string? targetName,CancellationToken ct) => GrabAsync(ct);
    public Task<CaptureResult> CaptureSequenceAsync(CaptureRequest request,CancellationToken ct) => Task.FromException<CaptureResult>(new NotSupportedException("SER recording is not implemented in this initial build."));
    public Task StopCaptureAsync(CancellationToken ct) => Task.CompletedTask;
    public CameraStatus GetStatus() => new(connection, capturing?CameraCaptureState.Capturing:CameraCaptureState.Idle,camera is { } c?Describe(c):null,connected?settings:null,null,0,connection==CameraConnectionState.Connected?"ASI camera connected.":null);
    public async ValueTask DisposeAsync(){try{await DisconnectAsync(CancellationToken.None);}catch{} gate.Dispose();}

    private async Task<ImageFrame> GrabAsync(CancellationToken ct) {
        await gate.WaitAsync(ct); try { var c=RequireCamera(); var w=(int)(c.MaxWidth/4)*4; var h=(int)(c.MaxHeight/4)*4; var bytes=new byte[checked(w*h*3)]; Check(Native.ASIGetVideoData(c.CameraID,bytes,bytes.Length,Math.Clamp((int)Math.Ceiling(settings.ExposureMilliseconds+3000),1000,30000)));
            var bitmap=BitmapSource.Create(w,h,96,96,PixelFormats.Rgb24,null,bytes,w*3); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var ms=new MemoryStream(); encoder.Save(ms);
            return new ImageFrame(ms.ToArray(),"image/png",w,h,DateTimeOffset.UtcNow,settings);
        } finally { gate.Release(); }
    }
    private CameraSettings ReadSettings(){var c=RequireCamera(); Check(Native.ASIGetControlValue(c.CameraID,Exposure,out var e,out _)); Check(Native.ASIGetControlValue(c.CameraID,Gain,out var g,out _)); settings=settings with {ExposureMilliseconds=e/1000d,Gain=g}; return settings;}
    private CameraCapabilities ReadCapabilities(CameraInfo c){ var n=0; Check(Native.ASIGetNumOfControls(c.CameraID,out n)); var exp=new ControlCaps();var gain=new ControlCaps(); for(var i=0;i<n;i++){Check(Native.ASIGetControlCaps(c.CameraID,i,out var x));if(x.ControlType==Exposure)exp=x;if(x.ControlType==Gain)gain=x;} return new(new(exp.MinValue/1000d,exp.MaxValue/1000d,0.001),new(gain.MinValue,gain.MaxValue,1),["RGB24","RAW8"],(int)c.MaxWidth,(int)c.MaxHeight,(int)c.MaxWidth,(int)c.MaxHeight); }
    private void StartVideo(){ if(!capturing){Check(Native.ASISetROIFormat(RequireCamera().CameraID,(int)(RequireCamera().MaxWidth/4)*4,(int)(RequireCamera().MaxHeight/4)*4,1,Rgb24));Check(Native.ASIStartVideoCapture(RequireCamera().CameraID));capturing=true;} }
    private CameraInfo RequireCamera()=>connected&&camera is { } c?c:throw new InvalidOperationException("Connect a camera first.");
    private static CameraDescriptor Describe(CameraInfo c)=>new(c.CameraID.ToString(),c.Name,c.Name,null,(int)c.MaxWidth,(int)c.MaxHeight,c.IsColorCam!=0,c.MechanicalShutter!=0,c.IsCoolerCam!=0);
    private static void Check(int rc){if(rc!=0)throw new InvalidOperationException($"ZWO ASI SDK error {rc}.");}

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Ansi)] private struct CameraInfo { [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Name; public int CameraID,MaxHeight,MaxWidth,IsColorCam,BayerPattern; [MarshalAs(UnmanagedType.ByValArray,SizeConst=16)] public int[] SupportedBins; [MarshalAs(UnmanagedType.ByValArray,SizeConst=8)] public int[] SupportedVideoFormat; public double PixelSize; public int MechanicalShutter,ST4Port,IsCoolerCam,IsUSB3Host,IsUSB3Camera; public float ElecPerADU; public int BitDepth,IsTriggerCam; [MarshalAs(UnmanagedType.ByValArray,SizeConst=16)] public byte[] Unused; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Ansi)] private struct ControlCaps { [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Name; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description; public int MaxValue,MinValue,DefaultValue,IsAutoSupported,IsWritable,ControlType; [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] Unused; }
    private static class Native {
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIGetNumOfConnectedCameras();
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIGetCameraProperty(out CameraInfo info,int index);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIGetCameraPropertyByID(int id,out CameraInfo info);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIOpenCamera(int id);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIInitCamera(int id);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASICloseCamera(int id);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIGetNumOfControls(int id,out int count);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIGetControlCaps(int id,int index,out ControlCaps caps);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIGetControlValue(int id,int type,out int value,out int auto);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASISetControlValue(int id,int type,int value,int auto);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASISetROIFormat(int id,int width,int height,int bin,int format);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIStartVideoCapture(int id);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIStopVideoCapture(int id);
        [DllImport("ASICamera2.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int ASIGetVideoData(int id,byte[] buffer,int size,int waitMs);
    }
}

