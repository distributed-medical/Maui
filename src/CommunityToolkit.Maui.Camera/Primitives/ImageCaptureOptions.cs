namespace CommunityToolkit.Maui.Primitives;

/// <summary>
/// Additional picture capture options
/// </summary>
public class ImageCaptureOptions
{
	/// <summary>
	/// Jpeg encoding quality (1 - 100)
	/// </summary>
	public int JpegQuality { get; set; } = 85;

	/// <summary>
	/// Override rotation angle in output image. 
	/// If null, the system will choose rotation angle based on device orientation. 
	/// If set, the system will ignore device orientation and use the specified rotation angle.
	/// </summary>
	public int? OverrideRotationAngle { get; set; }
}
