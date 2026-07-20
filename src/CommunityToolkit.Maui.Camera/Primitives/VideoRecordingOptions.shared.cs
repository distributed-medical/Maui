namespace CommunityToolkit.Maui.Primitives;

/// <summary>
/// Additional video recording options
/// </summary>
public class VideoRecordingOptions
{
	/// <summary>
	/// Id of H.264 video codec
	/// </summary>
	public const string H264VideoCodec = "H.264";

	/// <summary>
	/// Id of HEVC video codec
	/// </summary>
	public const string HevcVideoCodec = "HEVC";

	/// <summary>
	/// A specific temporary file to use for the video recording
	/// </summary>
	/// <remarks>If specified, it will NOT be deleted after completed recording</remarks>
	public string? TemporaryFilename { get; set; }

	/// <summary>
	/// The target frame rate to use for video recordings
	/// </summary>
	/// <remarks>Defaults to 0, which means the system will choose the optimal frame rate</remarks>
	public int TargetFrameRate { get; set; } = 0;

	/// <summary>
	/// Restrict resolution to maximum number of video lines (height) for video recordings
	/// </summary>
	/// <remarks>Defaults to 0, which means the maximum supported resolution</remarks>
	public int MaxResolutionLines { get; set; } = 0;

	/// <summary>
	/// Set to a specific video codec to use for video recordings
	/// </summary>
	public string? VideoCodec { get; set; } = null; // null means the system will choose the optimal codec

	/// <summary>
	/// Include audio in the recording
	/// </summary>
	public bool IncludeAudio { get; set; } = true;
}
