using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Asterion.Core;

namespace Asterion.Zwo;

public sealed class ZwoAsiCameraBackend : ICameraBackend
{
    private const int Exposure = 1, Gain = 0, Raw8 = 0, Rgb24 = 1;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CameraInfo? camera;
    private CameraSettings settings = new(100, 50, PixelFormat: "RGB24", Bin: 1);
    private CameraCapabilities? capabilities;
    private bool connected, capturing;
    private bool recording;
    private CancellationTokenSource? recordingCancellation;
    private CameraConnectionState connection = CameraConnectionState.Disconnected;

    public Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken ct) => Task.Run<IReadOnlyList<CameraDescriptor>>(() => {
        var n = Native.ASIGetNumOfConnectedCameras(); var result = new List<CameraDescriptor>();
        for (var i=0;i<n;i++) { Check(Native.ASIGetCameraProperty(out var c,i)); result.Add(Describe(c)); }
        return result;
    }, ct);

    public async Task ConnectAsync(string cameraId, CancellationToken ct) {
        await gate.WaitAsync(ct); try {
            if(!int.TryParse(cameraId,out var id)) throw new ArgumentException("Invalid ASI camera ID.");
            connection=CameraConnectionState.Connecting;
            var count=Native.ASIGetNumOfConnectedCameras();
            CameraInfo? discovered=null;
            for(var index=0;index<count;index++)
            {
                Check(Native.ASIGetCameraProperty(out var candidate,index));
                if(candidate.CameraID==id){discovered=candidate;break;}
            }
            var c=discovered??throw new InvalidOperationException($"ZWO ASI camera ID {id} is no longer connected. Refresh the camera list and try again.");
            camera=c;
            var opened=false;
            try
            {
                Check(Native.ASIOpenCamera(id)); opened=true;
                Check(Native.ASIInitCamera(id));
                connected=true;
                capabilities=ReadCapabilities(c);
                settings=ReadSettings();
                StartVideo();
                connection=CameraConnectionState.Connected;
            }
            catch
            {
                if(opened) Native.ASICloseCamera(id);
                connected=false;capturing=false;camera=null;capabilities=null;
                throw;
            }
        } catch { connection=CameraConnectionState.Faulted; throw; } finally { gate.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken ct) { recordingCancellation?.Cancel(); await gate.WaitAsync(ct); try { if(camera is { } c) { if(capturing){Native.ASIStopVideoCapture(c.CameraID);capturing=false;} Native.ASICloseCamera(c.CameraID); } connected=false; camera=null; capabilities=null; connection=CameraConnectionState.Disconnected; } finally { gate.Release(); } }
    public Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(capabilities ?? throw new InvalidOperationException("Connect a camera first."));
    public Task<CameraSettings> GetSettingsAsync(CancellationToken ct) => Task.FromResult(ReadSettings());
    public async Task SetSettingsAsync(CameraSettings value,CancellationToken ct) { await gate.WaitAsync(ct); try { var c=RequireCamera(); if(recording)throw new InvalidOperationException("Stop the SER recording before changing camera settings."); var reconfigure=value.Bin!=settings.Bin||value.PixelFormat!=settings.PixelFormat; if(reconfigure&&capturing){Check(Native.ASIStopVideoCapture(c.CameraID));capturing=false;} Check(Native.ASISetControlValue(c.CameraID,Exposure,(int)Math.Round(value.ExposureMilliseconds*1000),0)); Check(Native.ASISetControlValue(c.CameraID,Gain,(int)Math.Round(value.Gain),0)); settings=value with { PixelFormat=value.PixelFormat??"RGB24" }; if(reconfigure)StartVideo(); } finally { gate.Release(); } }
    public Task<ImageFrame> GetPreviewFrameAsync(CancellationToken ct) => GrabAsync(ct, false);
    public async Task<ImageFrame> CaptureSnapshotAsync(string? targetName,CancellationToken ct)
    {
        var frame=await GrabAsync(ct, true);
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"Asterion Observatory","Captures");
        Directory.CreateDirectory(folder);
        var safe=string.IsNullOrWhiteSpace(targetName)?"capture":string.Concat(targetName.Trim().Take(64).Select(ch=>char.IsLetterOrDigit(ch)||ch is '-' or '_'?ch:'_')).Trim('_');
        var path=Path.Combine(folder,$"{safe}_{frame.Timestamp:yyyyMMdd_HHmmss_fff}.png");
        await File.WriteAllBytesAsync(path,frame.Data,ct);
        return frame with { FilePath=path };
    }
    public async Task<CaptureResult> CaptureSequenceAsync(CaptureRequest request,CancellationToken ct)
    {
        if(string.Equals(request.Format,"AVI",StringComparison.OrdinalIgnoreCase)) return await CaptureAviSequenceAsync(request,ct);
        if(!string.Equals(request.Format,"SER",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Video recording supports SER and AVI formats.");
        var activeCamera=camera??throw new InvalidOperationException("Connect a camera first.");
        if(recording)throw new InvalidOperationException("A SER recording is already active.");
        var started=DateTimeOffset.UtcNow;var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"Asterion Observatory","Recordings");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,$"{SafeName(request.TargetName)}_{started:yyyyMMdd_HHmmss}.ser");using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);recordingCancellation=linked;recording=true;long frames=0;CameraSettings captureSettings=settings;
        var frameTimes=new List<long>();
        try
        {
            await using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);using var writer=new BinaryWriter(stream,System.Text.Encoding.ASCII,true);var bin=Math.Max(1,settings.Bin);var width=(int)(activeCamera.MaxWidth/bin/4)*4;var height=(int)(activeCamera.MaxHeight/bin/4)*4;var colorId=settings.PixelFormat=="RAW8"?(activeCamera.IsColorCam==0?0:activeCamera.BayerPattern switch{0=>8,1=>11,2=>9,3=>10,_=>0}):100;WriteSerHeader(writer,width,height,colorId,request.FrameLimit??0);var end=request.Duration is { } duration?started+duration:DateTimeOffset.MaxValue;
            while(!linked.IsCancellationRequested&&(request.FrameLimit is { } limit?frames<limit:DateTimeOffset.UtcNow<end))
            {
                (byte[] Data,int Width,int Height,int Stride,bool IsColor) raw;
                try { raw=await ReadRawFrameAsync(linked.Token); }
                catch(OperationCanceledException) when(linked.IsCancellationRequested) { break; }
                await stream.WriteAsync(raw.Data,CancellationToken.None);frameTimes.Add(DateTime.UtcNow.Ticks);frames++;
            }
            foreach(var time in frameTimes)writer.Write(time);
            writer.Flush();stream.Position=38;writer.Write(checked((int)frames));writer.Flush();await stream.FlushAsync(CancellationToken.None);
            return new(path,"SER",frames,started,DateTimeOffset.UtcNow,captureSettings);
        }
        finally{recording=false;recordingCancellation=null;}
    }
    private async Task<CaptureResult> CaptureAviSequenceAsync(CaptureRequest request,CancellationToken ct)
    {
        var activeCamera=camera??throw new InvalidOperationException("Connect a camera first.");
        if(recording)throw new InvalidOperationException("A video recording is already active.");
        var started=DateTimeOffset.UtcNow;var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"Asterion Observatory","Recordings");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,$"{SafeName(request.TargetName)}_{started:yyyyMMdd_HHmmss}.avi");using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);recordingCancellation=linked;recording=true;long frames=0;var captureSettings=settings;
        try
        {
            await using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
            using var writer=new BinaryWriter(stream,System.Text.Encoding.ASCII,true);
            var bin=Math.Max(1,settings.Bin);var width=(int)(activeCamera.MaxWidth/bin/4)*4;var height=(int)(activeCamera.MaxHeight/bin/4)*4;
            var header=WriteAviHeader(writer,width,height,Math.Clamp(settings.PreviewQuality,40,95));var end=request.Duration is { } duration?started+duration:DateTimeOffset.MaxValue;
            var index=new List<(uint Offset,uint Size)>();
            while(!linked.IsCancellationRequested&&(request.FrameLimit is { } limit?frames<limit:DateTimeOffset.UtcNow<end))
            {
                (byte[] Data,int Width,int Height,int Stride,bool IsColor) raw;
                try { raw=await ReadRawFrameAsync(linked.Token); } catch(OperationCanceledException) when(linked.IsCancellationRequested){break;}
                var bitmap=BitmapSource.Create(raw.Width,raw.Height,96,96,raw.IsColor?PixelFormats.Rgb24:PixelFormats.Gray8,null,raw.Data,raw.Stride);
                var encoder=new JpegBitmapEncoder{QualityLevel=Math.Clamp(captureSettings.PreviewQuality,40,95)};encoder.Frames.Add(BitmapFrame.Create(bitmap));using var jpeg=new MemoryStream();encoder.Save(jpeg);var bytes=jpeg.ToArray();
                var chunkStart=stream.Position;writer.Write(System.Text.Encoding.ASCII.GetBytes("00dc"));writer.Write(bytes.Length);writer.Write(bytes);if((bytes.Length&1)!=0)writer.Write((byte)0);
                index.Add((checked((uint)(chunkStart-header.MoviDataStart+4)),checked((uint)bytes.Length)));frames++;
            }
            var moviEnd=stream.Position;writer.Write(System.Text.Encoding.ASCII.GetBytes("idx1"));writer.Write(checked(index.Count*16));foreach(var entry in index){writer.Write(System.Text.Encoding.ASCII.GetBytes("00dc"));writer.Write(0x10u);writer.Write(entry.Offset);writer.Write(entry.Size);}
            var fileEnd=stream.Position;PatchAviHeader(writer,header,width,height,frames,started,fileEnd,moviEnd);await stream.FlushAsync(CancellationToken.None);
            return new(path,"AVI",frames,started,DateTimeOffset.UtcNow,captureSettings);
        }
        finally{recording=false;recordingCancellation=null;}
    }
    private readonly record struct AviHeaderPositions(long RiffSize,long HdrlSize,long AvihFrames,long StrhLength,long MoviSize,long MoviDataStart,long FileSize);
    private static AviHeaderPositions WriteAviHeader(BinaryWriter w,int width,int height,int quality)
    {
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));var riffSize=w.BaseStream.Position;w.Write(0);w.Write(System.Text.Encoding.ASCII.GetBytes("AVI "));
        w.Write(System.Text.Encoding.ASCII.GetBytes("LIST"));var hdrlSize=w.BaseStream.Position;w.Write(0);w.Write(System.Text.Encoding.ASCII.GetBytes("hdrl"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("avih"));w.Write(56);var microPos=w.BaseStream.Position;w.Write(33333);w.Write(0);w.Write(0);w.Write(0x10);var avihFrames=w.BaseStream.Position;w.Write(0);w.Write(0);w.Write(1);w.Write(0);w.Write(width);w.Write(height);for(var i=0;i<4;i++)w.Write(0);
        w.Write(System.Text.Encoding.ASCII.GetBytes("LIST"));var strlSize=w.BaseStream.Position;w.Write(0);w.Write(System.Text.Encoding.ASCII.GetBytes("strl"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("strh"));w.Write(56);w.Write(System.Text.Encoding.ASCII.GetBytes("vids"));w.Write(System.Text.Encoding.ASCII.GetBytes("MJPG"));w.Write(0);w.Write((short)0);w.Write((short)0);w.Write(0u);w.Write(1u);var fps=Math.Clamp((int)Math.Round(1_000_000d/33333),1,30);w.Write((uint)(1_000_000/fps));w.Write(0u);var strhLength=w.BaseStream.Position;w.Write(0);w.Write(0u);w.Write(0u);w.Write((short)0);w.Write((short)0);w.Write((short)width);w.Write((short)height);
        w.Write(System.Text.Encoding.ASCII.GetBytes("strf"));w.Write(40);w.Write(40);w.Write(width);w.Write(height);w.Write((short)1);w.Write((short)24);w.Write(System.Text.Encoding.ASCII.GetBytes("MJPG"));w.Write(width*height*3);w.Write(0);w.Write(0);w.Write(0);w.Write(0);
        var strlEnd=w.BaseStream.Position;w.BaseStream.Position=strlSize;w.Write(checked((int)(strlEnd-(strlSize+4))));w.BaseStream.Position=strlEnd;
        var hdrlEnd=w.BaseStream.Position;w.BaseStream.Position=hdrlSize;w.Write(checked((int)(hdrlEnd-(hdrlSize+4))));w.BaseStream.Position=hdrlEnd;
        w.Write(System.Text.Encoding.ASCII.GetBytes("LIST"));var moviSize=w.BaseStream.Position;w.Write(0);w.Write(System.Text.Encoding.ASCII.GetBytes("movi"));var dataStart=w.BaseStream.Position;
        return new(riffSize,hdrlSize,avihFrames,strhLength,moviSize,dataStart,0);
    }
    private static void PatchAviHeader(BinaryWriter w,AviHeaderPositions p,int width,int height,long frames,DateTimeOffset started,long fileEnd,long moviEnd)
    {
        var elapsed=Math.Max(0.001,(DateTimeOffset.UtcNow-started).TotalSeconds);var fps=Math.Clamp(frames/elapsed,1,30);w.BaseStream.Position=p.RiffSize;w.Write(checked((int)(fileEnd-8)));w.BaseStream.Position=p.RiffSize+32;w.Write(checked((int)Math.Round(1_000_000/fps)));w.BaseStream.Position=p.AvihFrames;w.Write(checked((int)frames));w.BaseStream.Position=p.StrhLength;w.Write(checked((int)frames));w.BaseStream.Position=p.MoviSize;w.Write(checked((int)(moviEnd-(p.MoviSize+4))));w.BaseStream.Position=fileEnd;
    }
    public Task StopCaptureAsync(CancellationToken ct){recordingCancellation?.Cancel();return Task.CompletedTask;}
    public CameraStatus GetStatus() => new(connection, recording?CameraCaptureState.Capturing:CameraCaptureState.Idle,camera is { } c?Describe(c):null,connected?settings:null,null,0,connection==CameraConnectionState.Connected?"ASI camera connected.":null);
    public async ValueTask DisposeAsync(){try{await DisconnectAsync(CancellationToken.None);}catch{} gate.Dispose();}

    private async Task<ImageFrame> GrabAsync(CancellationToken ct,bool still){var raw=await ReadRawFrameAsync(ct);var metrics=Analyze(raw.Data,raw.Width,raw.Height,raw.IsColor);var bitmap=BitmapSource.Create(raw.Width,raw.Height,96,96,raw.IsColor?PixelFormats.Rgb24:PixelFormats.Gray8,null,raw.Data,raw.Stride);BitmapEncoder encoder;string mimeType;if(still){encoder=new PngBitmapEncoder();mimeType="image/png";}else{encoder=new JpegBitmapEncoder{QualityLevel=Math.Clamp(settings.PreviewQuality,40,95)};mimeType="image/jpeg";}encoder.Frames.Add(BitmapFrame.Create(bitmap));using var ms=new MemoryStream();encoder.Save(ms);return new(ms.ToArray(),mimeType,raw.Width,raw.Height,DateTimeOffset.UtcNow,settings,metrics);}
    private async Task<(byte[] Data,int Width,int Height,int Stride,bool IsColor)> ReadRawFrameAsync(CancellationToken ct){await gate.WaitAsync(ct);try{var c=RequireCamera();var bin=Math.Max(1,settings.Bin);var w=(int)(c.MaxWidth/bin/4)*4;var h=(int)(c.MaxHeight/bin/4)*4;var color=settings.PixelFormat!="RAW8";var stride=w*(color?3:1);var bytes=new byte[checked(stride*h)];Check(Native.ASIGetVideoData(c.CameraID,bytes,bytes.Length,Math.Clamp((int)Math.Ceiling(settings.ExposureMilliseconds+1000),1000,30000)));return(bytes,w,h,stride,color);}finally{gate.Release();}}
    private static string SafeName(string value)=>string.Concat((string.IsNullOrWhiteSpace(value)?"recording":value).Trim().Take(64).Select(ch=>char.IsLetterOrDigit(ch)||ch is '-' or '_'?ch:'_')).Trim('_');
    private static void WriteSerHeader(BinaryWriter w,int width,int height,int colorId,int frameCount){w.Write(System.Text.Encoding.ASCII.GetBytes("LUCAM-RECORDER"));w.Write(0x019);w.Write(colorId);w.Write(1);w.Write(width);w.Write(height);w.Write(8);w.Write(frameCount);WriteSerText(w,"Asterion Observatory",40);WriteSerText(w,"ZWO ASI",40);WriteSerText(w,"",40);w.Write(DateTime.Now.Ticks);w.Write(DateTime.UtcNow.Ticks);}
    private static void WriteSerText(BinaryWriter w,string value,int length){var bytes=System.Text.Encoding.ASCII.GetBytes(value);Array.Resize(ref bytes,length);w.Write(bytes);}
    private CameraSettings ReadSettings(){var c=RequireCamera(); Check(Native.ASIGetControlValue(c.CameraID,Exposure,out var e,out _)); Check(Native.ASIGetControlValue(c.CameraID,Gain,out var g,out _)); settings=settings with {ExposureMilliseconds=e/1000d,Gain=g}; return settings;}
    private CameraCapabilities ReadCapabilities(CameraInfo c){ var n=0; Check(Native.ASIGetNumOfControls(c.CameraID,out n)); var exp=new ControlCaps();var gain=new ControlCaps(); for(var i=0;i<n;i++){Check(Native.ASIGetControlCaps(c.CameraID,i,out var x));if(x.ControlType==Exposure)exp=x;if(x.ControlType==Gain)gain=x;} var bins=c.SupportedBins?.Where(x=>x>0).Distinct().Order().ToArray()??[1];if(bins.Length==0)bins=[1];var formats=c.SupportedVideoFormat?.Where(x=>x==Rgb24||x==Raw8).Distinct().Select(x=>x==Raw8?"RAW8":"RGB24").ToArray()??[];if(formats.Length==0)formats=["RGB24"];return new(new(exp.MinValue/1000d,exp.MaxValue/1000d,0.001),new(gain.MinValue,gain.MaxValue,1),formats,(int)c.MaxWidth,(int)c.MaxHeight,(int)c.MaxWidth,(int)c.MaxHeight,bins); }
    private void StartVideo(){ if(!capturing){var c=RequireCamera();var bin=Math.Max(1,settings.Bin);var w=(int)(c.MaxWidth/bin/4)*4;var h=(int)(c.MaxHeight/bin/4)*4;var format=settings.PixelFormat=="RAW8"?Raw8:Rgb24;Check(Native.ASISetROIFormat(c.CameraID,w,h,bin,format));Check(Native.ASIStartVideoCapture(c.CameraID));capturing=true;} }
    private CameraInfo RequireCamera()=>connected&&camera is { } c?c:throw new InvalidOperationException("Connect a camera first.");
    private static CameraDescriptor Describe(CameraInfo c)=>new(c.CameraID.ToString(),c.Name,c.Name,null,(int)c.MaxWidth,(int)c.MaxHeight,c.IsColorCam!=0,c.MechanicalShutter!=0,c.IsCoolerCam!=0);
    private static void Check(int rc){if(rc==0)return;var detail=rc switch{1=>"no camera at that index",2=>"invalid camera ID",3=>"unsupported control",4=>"camera is not open",5=>"camera was removed",9=>"unsupported image format",11=>"camera frame timed out",12=>"invalid capture sequence",16=>"camera rejected the requested setting",_=>"native SDK operation failed"};throw new InvalidOperationException($"ZWO ASI SDK error {rc}: {detail}.");}

    private static ImageMetrics Analyze(byte[] rgb,int width,int height,bool color=true)
    {
        Span<int> histogram=stackalloc int[256]; long sum=0,samples=0,clipped=0; double lapSum=0,lapSquares=0; long lapSamples=0; var candidates=0;
        for(var y=4;y<height-4;y+=4) for(var x=4;x<width-4;x+=4)
        {
            var i=(y*width+x)*(color?3:1); var l=color?(int)(0.2126*rgb[i]+0.7152*rgb[i+1]+0.0722*rgb[i+2]):rgb[i]; histogram[l]++;sum+=l;samples++;if(l>=250)clipped++;
            int L(int xx,int yy){var p=(yy*width+xx)*(color?3:1);return color?(int)(0.2126*rgb[p]+0.7152*rgb[p+1]+0.0722*rgb[p+2]):rgb[p];}
            var lap=4*l-L(x-4,y)-L(x+4,y)-L(x,y-4)-L(x,y+4);lapSum+=lap;lapSquares+=(double)lap*lap;lapSamples++;
            if(l>24&&l>=L(x-4,y)&&l>=L(x+4,y)&&l>=L(x,y-4)&&l>=L(x,y+4)) candidates++;
        }
        if(samples==0)return new(0,0,0,0,0);
        var target=(long)Math.Ceiling(samples*0.95);long cumulative=0;var p95=0;for(;p95<255;p95++){cumulative+=histogram[p95];if(cumulative>=target)break;}
        var meanLap=lapSum/Math.Max(1,lapSamples);var focus=Math.Max(0,lapSquares/Math.Max(1,lapSamples)-meanLap*meanLap);
        return new((double)sum/samples,p95,(double)clipped/samples,focus,candidates);
    }

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

