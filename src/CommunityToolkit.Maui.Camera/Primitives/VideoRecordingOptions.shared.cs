using System;
using System.Collections.Generic;
using System.Text;

namespace CommunityToolkit.Maui.Primitives;

/// <summary>
/// Additional video recording options
/// </summary>
public class VideoRecordingOptions
{
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
}
