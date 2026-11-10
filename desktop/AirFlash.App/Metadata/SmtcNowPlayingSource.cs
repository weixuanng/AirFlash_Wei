using System.Security.Cryptography;
using AirFlash.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
namespace AirFlash.App.Metadata;

/// <summary>
/// Reads Now Playing from the Windows system media transport controls (SMTC).
/// </summary>
/// <remarks>
/// SMTC is the only supported way for a desktop process to see what the current
/// media app is playing; there is no Win32 equivalent. Every call is wrapped so an
/// environment that refuses the session request (no media app, policy, or a Windows
/// build without the contract) simply produces no metadata instead of failing the
/// stream.
/// </remarks>
public sealed class SmtcNowPlayingSource(Action<string>? log = null) : INowPlayingSource
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    /// <summary>Longest side of the artwork sent to the speaker, in pixels.</summary>
    private const uint ArtworkSize = 512;
    private static readonly string ArtworkDirectory = Path.Combine(Path.GetTempPath(), "Airplay");
    private string _artworkTrack = "", _artworkPath = "";
    private bool _artworkLogged;
    /// <summary>
    /// The current thumbnail as a JPEG file. Read once per track, and again on later
    /// polls while the player has not published one yet.
    /// </summary>
    private async Task<string> ArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties properties, CancellationToken cancellation)
    {
        var track = $"{properties.Title}\n{properties.Artist}\n{properties.AlbumTitle}";
        if (track == _artworkTrack && _artworkPath.Length > 0 && File.Exists(_artworkPath)) return _artworkPath;
        if (track != _artworkTrack) { _artworkTrack = track; _artworkPath = ""; }
        if (properties.Thumbnail is not { } thumbnail) return "";
        try
        {
            using var source = await thumbnail.OpenReadAsync().AsTask(cancellation).ConfigureAwait(false);
            var decoder = await BitmapDecoder.CreateAsync(source).AsTask(cancellation).ConfigureAwait(false);
            var longest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            if (longest == 0) return "";
            var scale = Math.Min(1.0, (double)ArtworkSize / longest);
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            // JPEG has no alpha: decode to opaque BGRA so a transparent logo does not turn black on black.
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellation).ConfigureAwait(false);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output).AsTask(cancellation).ConfigureAwait(false);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform.ScaledWidth, transform.ScaledHeight, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync().AsTask(cancellation).ConfigureAwait(false);
            var bytes = new byte[output.Size];
            output.Seek(0);
            using (var reader = new DataReader(output))
            {
                await reader.LoadAsync((uint)bytes.Length).AsTask(cancellation).ConfigureAwait(false);
                reader.ReadBytes(bytes);
            }
            Directory.CreateDirectory(ArtworkDirectory);
            var path = Path.Combine(ArtworkDirectory, $"artwork-{Convert.ToHexString(SHA256.HashData(bytes))[..16]}.jpg");
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes, cancellation).ConfigureAwait(false);
            foreach (var old in Directory.EnumerateFiles(ArtworkDirectory, "artwork-*.jpg"))
                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (!_artworkLogged) { _artworkLogged = true; log?.Invoke($"Now Playing artwork: {transform.ScaledWidth}x{transform.ScaledHeight}; {bytes.Length} bytes"); }
            return _artworkPath = path;
        }
        catch (OperationCanceledException) { throw; }
        // Artwork is decoration: a thumbnail that cannot be read or encoded is skipped.
        catch (Exception error)
        {
            if (!_artworkLogged) { _artworkLogged = true; log?.Invoke($"Now Playing artwork unavailable: {error.Message}"); }
            return "";
        }
    }
    public async Task ExecuteAsync(RemoteCommand command, CancellationToken cancellation)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellation).ConfigureAwait(false);
            var session = _manager.GetCurrentSession();
            if (session is null) return;
            if (command.Name == "play") await session.TryPlayAsync().AsTask(cancellation).ConfigureAwait(false);
            else if (command.Name == "pause") await session.TryPauseAsync().AsTask(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { log?.Invoke($"Remote media command failed: {error.Message}"); }
    }
    public async Task<NowPlaying?> ReadAsync(CancellationToken cancellation)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellation).ConfigureAwait(false);
            var session = _manager.GetCurrentSession();
            if (session is null) return null;
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellation).ConfigureAwait(false);
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return new AirFlash.Core.NowPlaying(
                properties.Title ?? "",
                properties.Artist ?? "",
                properties.AlbumTitle ?? "",
                properties.Genres?.FirstOrDefault() ?? "",
                playing,
                Math.Max(0, (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds),
                Math.Max(0, (long)(timeline.Position - timeline.StartTime).TotalMilliseconds),
                await ArtworkAsync(properties, cancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            log?.Invoke(L.Format("Now Playing source unavailable: {0}", error.Message));
            // Forget the manager so a later poll can recreate it after the media
            // session restarts.
            _manager = null;
            return null;
        }
    }
}
